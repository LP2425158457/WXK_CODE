using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Core.Metadata;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.App.Data;
using Kingdee.BOS.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Collections;
using System.ComponentModel;
using Kingdee.BOS.Core.DynamicForm.PlugIn;
using Kingdee.BOS.Core.DynamicForm.Operation;
using System.Data;
using Kingdee.BOS.Core.List;
using Kingdee.BOS.Core.Metadata.ConvertElement.ServiceArgs;
using Kingdee.BOS.Core.Metadata.EntityElement;
using Kingdee.BOS.Core.Metadata.FieldElement;
using Kingdee.BOS.ServiceHelper;
using Kingdee.BOS.Orm;

namespace LP.WXK.K3.App.ServicePlugIn
{
    [Description("【操作插件】收款认领单保存后自动提交、审核；同批认款齐套时下推合并生成收款单"), HotUpdate]
    public class RecClaimToReceiveBillOperationPlugIn : AbstractOperationServicePlugIn
    {
        private const string TARGET_FORMID = "AR_RECEIVEBILL";

        private const string CONVERT_RULE_ID = "CN_RecClaimBillToRecBill";

        private const string AutoPushMinTransDate = "2026-06-15";

        /// <summary>
        /// 金额比较容差（与标准币别小数位一致，避免舍入导致误判）
        /// </summary>
        private const decimal AmountTolerance = 0.01m;

        /// <summary>
        /// 已审核（标准单据状态，与原先下推条件一致）
        /// </summary>
        private const string DocumentStatusAudited = "C";

        [ThreadStatic]
        private static bool syncingClaimDetail;

        public override void AfterExecuteOperationTransaction(AfterExecuteOperationTransaction e)
        {
            base.AfterExecuteOperationTransaction(e);

            if (e.DataEntitys == null || e.DataEntitys.Length == 0)
            {
                return;
            }

            if (!IsSaveOperation() || syncingClaimDetail)
            {
                return;
            }

            List<long> savedBillIds = e.DataEntitys
                .Where(o => o != null)
                .Select(o => Convert.ToInt64(o["Id"]))
                .Distinct()
                .ToList();

            if (savedBillIds.Count == 0)
            {
                return;
            }

            var formID = this.BusinessInfo.GetForm().Id;
            string sourceFormId = Convert.ToString(formID);
            var sourceMeta = MetaDataServiceHelper.Load(this.Context, sourceFormId) as FormMetadata;
            if (sourceMeta == null)
            {
                throw new Exception($"未加载到收款认领单元数据，请检查表单 FormId={sourceFormId}！");
            }

            OperateOption claimOption = OperateOption.Create();
            claimOption.SetIgnoreWarning(true);

            SyncClaimDetailByFlowEntry(savedBillIds, sourceMeta, claimOption);

            foreach (long bid in savedBillIds)
            {
                SubmitAndAuditClaimBillIfNeeded(bid, sourceMeta, claimOption);
            }

            var processedBatchKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (long billId in savedBillIds)
            {
                BatchKey key = TryGetBatchKey(billId);
                if (key == null)
                {
                    continue;
                }

                string batchKeyStr = key.ToToken();
                if (processedBatchKeys.Contains(batchKeyStr))
                {
                    continue;
                }

                processedBatchKeys.Add(batchKeyStr);

                List<long> sameBatchIds = GetSameBatchAuditedUnpushedBillIds(key);
                if (sameBatchIds == null || sameBatchIds.Count == 0)
                {
                    continue;
                }

                List<long> eligibleIds = sameBatchIds.Where(id => ShouldTryGenerateReceiveBill(id)).ToList();
                eligibleIds = FilterByAutoPushTransDate(eligibleIds);
                if (eligibleIds.Count == 0)
                {
                    continue;
                }

                if (!TryValidateBatchAmounts(eligibleIds, key.RecAmount, out _))
                {
                    continue;
                }

                PushToReceiveBill(eligibleIds, sourceFormId);
            }
        }

        /// <summary>
        /// 是否尝试自动下推收款单：有交易流水号且尚未生成收款单。不满足时静默跳过，不影响保存与提交审核。
        /// </summary>
        private bool ShouldTryGenerateReceiveBill(long billId)
        {
            if (ValidateAlreadyPushed(billId))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(GetBankSeqNo(billId));
        }

        private bool IsSaveOperation()
        {
            return this.FormOperation != null
                && string.Equals(this.FormOperation.Operation, "Save", StringComparison.OrdinalIgnoreCase);
        }

        private string GetDocumentStatus(long billId)
        {
            try
            {
                string sql = $"SELECT FDocumentStatus FROM T_CN_RECCLAIMBILL WHERE FID = {billId}";
                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToString(reader["FDocumentStatus"]) ?? "";
                    }
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        /// <summary>
        /// 保存后尽量将认领单提交并审核到已审核；已为 C 则跳过，为审核中则只尝试审核。
        /// </summary>
        private void SubmitAndAuditClaimBillIfNeeded(long billId, FormMetadata sourceMeta, OperateOption opt)
        {
            string st = GetDocumentStatus(billId);
            if (string.Equals(st, DocumentStatusAudited, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            object[] one = new object[] { billId };

            if (!string.Equals(st, "B", StringComparison.OrdinalIgnoreCase))
            {
                var submitResult = BusinessDataServiceHelper.Submit(this.Context, sourceMeta.BusinessInfo, one, "Submit", opt);
                if (!submitResult.IsSuccess)
                {
                    var msg = GetOperationErrorMessage(submitResult, "提交失败");
                    throw new Exception($"收款认领单 {GetBillNo(billId)} 提交失败：{msg}");
                }
            }

            st = GetDocumentStatus(billId);
            if (string.Equals(st, DocumentStatusAudited, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var auditResult = BusinessDataServiceHelper.Audit(this.Context, sourceMeta.BusinessInfo, one, opt);
            if (!auditResult.IsSuccess)
            {
                var msg = GetOperationErrorMessage(auditResult, "审核失败");
                throw new Exception($"收款认领单 {GetBillNo(billId)} 审核失败：{msg}");
            }
        }

        private string GetBillNo(long billId)
        {
            try
            {
                string sql = string.Format("SELECT FBILLNO FROM T_CN_RECCLAIMBILL WHERE FID = {0}", billId);
                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToString(reader["FBILLNO"]);
                    }
                }
            }
            catch (Exception)
            {
            }
            return billId.ToString();
        }

        /// <summary>
        /// 同批维度：流水总金额 + 付款单位 + 收款组织 + 银行账号（流水明细首行）+ 结算方式（流水明细首行，对应业务上的付款/结算方式）
        /// </summary>
        private BatchKey TryGetBatchKey(long billId)
        {
            try
            {
                string sql = string.Format(@"
SELECT h.FRECAMOUNT, h.FPAYUNIT, h.FRECORGID, e.FACCOUNTID, e.FSETTLETYPEID
FROM T_CN_RECCLAIMBILL h
INNER JOIN (
    SELECT FID, MIN(FENTRYID) AS MinEntryId
    FROM T_CN_RECCLAIMBILLENTRY
    GROUP BY FID
) x ON h.FID = x.FID
INNER JOIN T_CN_RECCLAIMBILLENTRY e ON e.FENTRYID = x.MinEntryId
WHERE h.FID = {0}", billId);

                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return new BatchKey
                    {
                        RecAmount = Convert.ToDecimal(reader["FRECAMOUNT"]),
                        PayUnit = Convert.ToInt32(reader["FPAYUNIT"]),
                        RecOrgId = Convert.ToInt32(reader["FRECORGID"]),
                        AccountId = Convert.ToInt32(reader["FACCOUNTID"]),
                        SettleTypeId = Convert.ToInt32(reader["FSETTLETYPEID"])
                    };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 查询与当前单同批、已审核且未生成收款单的认领单。
        /// </summary>
        private List<long> GetSameBatchAuditedUnpushedBillIds(BatchKey key)
        {
            var list = new List<long>();
            string recAmtStr = key.RecAmount.ToString(CultureInfo.InvariantCulture);

            string sql = string.Format(@"
SELECT h.FID
FROM T_CN_RECCLAIMBILL h
INNER JOIN (
    SELECT FID, MIN(FENTRYID) AS MinEntryId
    FROM T_CN_RECCLAIMBILLENTRY
    GROUP BY FID
) x ON h.FID = x.FID
INNER JOIN T_CN_RECCLAIMBILLENTRY e ON e.FENTRYID = x.MinEntryId
WHERE h.FRECAMOUNT = {0}
  AND h.FPAYUNIT = {1}
  AND h.FRECORGID = {2}
  AND e.FACCOUNTID = {3}
  AND e.FSETTLETYPEID = {4}
  AND h.FDocumentStatus IN (N'B', N'C')
  AND NOT EXISTS (
      SELECT 1 FROM T_AR_RECEIVEBILLSRCENTRY src WHERE src.FSRCBILLTYPEID = 'CN_RECCLAIMBILL' AND src.FSRCBILLID = h.FID
  )",
                recAmtStr,
                key.PayUnit,
                key.RecOrgId,
                key.AccountId,
                key.SettleTypeId);

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                while (reader.Read())
                {
                    list.Add(Convert.ToInt64(reader["FID"]));
                }
            }

            return list;
        }

        /// <summary>
        /// 同批内 FRECAMOUNT 一致，且已认领金额之和等于流水总金额。
        /// </summary>
        private bool TryValidateBatchAmounts(List<long> billIds, decimal expectedRecAmount, out string error)
        {
            error = null;
            if (billIds == null || billIds.Count == 0)
            {
                return false;
            }

            try
            {
                string ids = string.Join(",", billIds);
                decimal sumClaim = 0m;
                string sql = string.Format(
                    "SELECT FCLAIMAMOUNT, FRECAMOUNT FROM T_CN_RECCLAIMBILL WHERE FID IN ({0})",
                    ids);

                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    while (reader.Read())
                    {
                        sumClaim += Convert.ToDecimal(reader["FCLAIMAMOUNT"]);
                        decimal rec = Convert.ToDecimal(reader["FRECAMOUNT"]);
                        if (Math.Abs(rec - expectedRecAmount) > AmountTolerance)
                        {
                            error = "同批单据流水总金额不一致";
                            return false;
                        }
                    }
                }

                if (Math.Abs(sumClaim - expectedRecAmount) > AmountTolerance)
                {
                    error = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 获取认领单分录上的交易流水号（取首行）
        /// </summary>
        private string GetBankSeqNo(long billId)
        {
            try
            {
                string sql = string.Format(@"
SELECT TOP 1 e.FBNKSEQNO
FROM T_CN_RECCLAIMBILL h
INNER JOIN T_CN_RECCLAIMBILLENTRY e ON h.FID = e.FID
WHERE h.FID = {0}
ORDER BY e.FENTRYID", billId);

                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToString(reader["FBNKSEQNO"]) ?? "";
                    }
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        private List<long> FilterByAutoPushTransDate(List<long> billIds)
        {
            if (billIds == null || billIds.Count == 0)
            {
                return new List<long>();
            }

            string ids = string.Join(",", billIds.Distinct());
            string sql = string.Format(@"
SELECT DISTINCT e.FID
FROM T_CN_RECCLAIMBILLENTRY e
LEFT JOIN T_CN_BANKCASHFLOW b ON e.FBNKSEQNO = b.FSETTLENO
WHERE e.FID IN ({0})
  AND NOT EXISTS (
      SELECT 1
      FROM T_CN_RECCLAIMBILLENTRY e2
      LEFT JOIN T_CN_BANKCASHFLOW b2 ON e2.FBNKSEQNO = b2.FSETTLENO
      WHERE e2.FID = e.FID
        AND b2.FTRANSDATE < '{1}'
  )", ids, AutoPushMinTransDate);

            var result = new List<long>();
            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                while (reader.Read())
                {
                    result.Add(Convert.ToInt64(reader["FID"]));
                }
            }

            return result;
        }

        private bool ValidateAlreadyPushed(long billId)
        {
            try
            {
                string sql = $"SELECT 1 FROM T_AR_RECEIVEBILLSRCENTRY WHERE FSRCBILLTYPEID = 'CN_RECCLAIMBILL' and FSRCBILLID = {billId}";
                using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
                {
                    return reader.Read();
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private string GetOperationErrorMessage(object result, string defaultMessage)
        {
            if (result == null)
            {
                return defaultMessage;
            }

            var messages = new List<string>();
            AppendOperationMessages(messages, result, "OperateResult");
            AppendOperationMessages(messages, result, "ValidationErrors");

            if (messages.Count == 0)
            {
                return defaultMessage;
            }

            return string.Join("；", messages.Distinct());
        }

        private void AppendOperationMessages(List<string> messages, object result, string propertyName)
        {
            var property = result.GetType().GetProperty(propertyName);
            if (property == null)
            {
                return;
            }

            var values = property.GetValue(result, null) as IEnumerable;
            if (values == null)
            {
                return;
            }

            foreach (object item in values)
            {
                if (item == null)
                {
                    continue;
                }

                var messageProperty = item.GetType().GetProperty("Message");
                if (messageProperty == null)
                {
                    continue;
                }

                string message = Convert.ToString(messageProperty.GetValue(item, null));
                if (!string.IsNullOrWhiteSpace(message))
                {
                    messages.Add(message);
                }
            }
        }

        private void SyncClaimDetailByFlowEntry(List<long> sourceBillIds, FormMetadata sourceMeta, OperateOption saveOption)
        {
            if (sourceBillIds == null || sourceBillIds.Count == 0 || sourceMeta == null)
            {
                return;
            }

            object[] billIds = sourceBillIds.Cast<object>().ToArray();
            DynamicObject[] claimBills = BusinessDataServiceHelper.Load(this.Context, billIds, sourceMeta.BusinessInfo.GetDynamicObjectType());
            if (claimBills == null || claimBills.Length == 0)
            {
                return;
            }

            Entity detailEntity = sourceMeta.BusinessInfo.GetEntity("FRECCMENTRYDETAIL");
            if (detailEntity == null)
            {
                throw new Exception("未找到收款认领单的收款认领详情实体，请检查源单字段标识 FRECCMENTRYDETAIL");
            }

            BaseDataField settleTypeField = sourceMeta.BusinessInfo.GetField("FCMSETTLETYPEID") as BaseDataField;
            if (settleTypeField == null)
            {
                throw new Exception("未找到收款认领单的收款认领详情结算方式字段，请检查源单字段标识 FCMSETTLETYPEID");
            }

            Dictionary<long, ClaimFlowBankInfo> flowMap = GetSourceFlowBankInfoMap(sourceBillIds);
            if (flowMap.Count == 0)
            {
                return;
            }

            bool changed = false;
            foreach (DynamicObject claimBill in claimBills)
            {
                long billId = Convert.ToInt64(claimBill["Id"]);
                ClaimFlowBankInfo flowInfo;
                if (!flowMap.TryGetValue(billId, out flowInfo))
                {
                    continue;
                }

                DynamicObjectCollection details = detailEntity.DynamicProperty.GetValue(claimBill) as DynamicObjectCollection;
                if (details == null || details.Count == 0)
                {
                    continue;
                }

                foreach (DynamicObject detail in details)
                {
                    if (flowInfo.SettleTypeId > 0)
                    {
                        object oldSettleTypeIdObj = settleTypeField.RefIDDynamicProperty.GetValue(detail);
                        int oldSettleTypeId = oldSettleTypeIdObj == null ? 0 : Convert.ToInt32(oldSettleTypeIdObj);
                        if (oldSettleTypeId != flowInfo.SettleTypeId)
                        {
                            settleTypeField.RefIDDynamicProperty.SetValue(detail, flowInfo.SettleTypeId);
                            changed = true;
                        }
                    }

                    changed = SetDynamicValueIfExists(detail, "FACCOUNTID", flowInfo.AccountId) || changed;
                    changed = SetDynamicValueIfExists(detail, "FOPPOSITEBANKACCOUNT", flowInfo.OppositeBankAccount) || changed;
                    changed = SetDynamicValueIfExists(detail, "FOPPOSITEBANKNAME", flowInfo.OppositeBankName) || changed;
                    changed = SetDynamicValueIfExists(detail, "FOPPOSITECCOUNTNAME", flowInfo.OppositeAccountName) || changed;
                }
            }

            if (!changed)
            {
                return;
            }

            try
            {
                syncingClaimDetail = true;
                var saveResult = BusinessDataServiceHelper.Save(this.Context, sourceMeta.BusinessInfo, claimBills, saveOption, "Save");
                if (!saveResult.IsSuccess)
                {
                    var errorMsg = GetOperationErrorMessage(saveResult, "保存失败");
                    throw new Exception($"保存收款认领单认领详情银行信息失败：{errorMsg}");
                }
            }
            finally
            {
                syncingClaimDetail = false;
            }
        }

        private bool SetDynamicValueIfExists(DynamicObject data, string propertyName, object value)
        {
            if (data == null || !data.DynamicObjectType.Properties.ContainsKey(propertyName))
            {
                return false;
            }

            object oldValue = data[propertyName];
            string oldText = Convert.ToString(oldValue) ?? string.Empty;
            string newText = Convert.ToString(value) ?? string.Empty;
            if (oldText == newText)
            {
                return false;
            }

            data[propertyName] = value;
            return true;
        }

        private Dictionary<long, ClaimFlowBankInfo> GetSourceFlowBankInfoMap(List<long> sourceBillIds)
        {
            var map = new Dictionary<long, ClaimFlowBankInfo>();
            string sourceIds = string.Join(",", sourceBillIds.Distinct());
            string sql = string.Format(@"
SELECT e.FID, e.FSETTLETYPEID, e.FACCOUNTID, acc.FNUMBER AS FACCOUNTNUMBER, acc_l.FNAME AS FACCOUNTNAME, e.FOPPOSITEBANKACCOUNT, e.FOPPOSITEBANKNAME, e.FOPPOSITECCOUNTNAME
FROM T_CN_RECCLAIMBILLENTRY e
LEFT JOIN T_BD_ACCOUNT acc ON e.FACCOUNTID = acc.FACCTID
LEFT JOIN T_BD_ACCOUNT_L acc_l ON acc.FACCTID = acc_l.FACCTID AND acc_l.FLOCALEID = 2052
INNER JOIN (
    SELECT FID, MIN(FENTRYID) AS MinEntryId
    FROM T_CN_RECCLAIMBILLENTRY
    WHERE FID IN ({0})
    GROUP BY FID
) x ON e.FENTRYID = x.MinEntryId", sourceIds);

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                while (reader.Read())
                {
                    long sourceBillId = Convert.ToInt64(reader["FID"]);
                    map[sourceBillId] = new ClaimFlowBankInfo
                    {
                        SettleTypeId = Convert.ToInt32(reader["FSETTLETYPEID"]),
                        AccountId = Convert.ToInt32(reader["FACCOUNTID"]),
                        AccountNumber = Convert.ToString(reader["FACCOUNTNUMBER"]),
                        AccountName = Convert.ToString(reader["FACCOUNTNAME"]),
                        OppositeBankAccount = Convert.ToString(reader["FOPPOSITEBANKACCOUNT"]),
                        OppositeBankName = Convert.ToString(reader["FOPPOSITEBANKNAME"]),
                        OppositeAccountName = Convert.ToString(reader["FOPPOSITECCOUNTNAME"])
                    };
                }
            }

            return map;
        }

        private Dictionary<long, int> GetSourceSettleTypeMap(List<long> sourceBillIds)
        {
            var map = new Dictionary<long, int>();
            string sourceIds = string.Join(",", sourceBillIds.Distinct());
            string sql = string.Format(@"
SELECT e.FID, e.FSETTLETYPEID
FROM T_CN_RECCLAIMBILLENTRY e
INNER JOIN (
    SELECT FID, MIN(FENTRYID) AS MinEntryId
    FROM T_CN_RECCLAIMBILLENTRY
    WHERE FID IN ({0})
    GROUP BY FID
) x ON e.FENTRYID = x.MinEntryId", sourceIds);

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                while (reader.Read())
                {
                    long sourceBillId = Convert.ToInt64(reader["FID"]);
                    int settleTypeId = Convert.ToInt32(reader["FSETTLETYPEID"]);
                    map[sourceBillId] = settleTypeId;
                }
            }

            return map;
        }

        private void FillReceiveBillBankInfo(DynamicObject[] receiveBills, List<long> sourceBillIds, FormMetadata targetBillMeta)
        {
            if (receiveBills == null || receiveBills.Length == 0 || sourceBillIds == null || sourceBillIds.Count == 0 || targetBillMeta == null)
            {
                return;
            }

            Dictionary<long, ClaimFlowBankInfo> flowMap = GetSourceFlowBankInfoMap(sourceBillIds);
            if (flowMap.Count == 0)
            {
                return;
            }

            foreach (DynamicObject receiveBill in receiveBills)
            {
                FillReceiveBillEntryBankInfo(targetBillMeta, receiveBill, "RECEIVEBILLENTRY", flowMap);
                FillReceiveBillEntryBankInfo(targetBillMeta, receiveBill, "RECEIVEBILLREC", flowMap);
                FillReceiveBillEntryBankInfo(targetBillMeta, receiveBill, "RECEIVEBILLSRCENTRY", flowMap);
            }
        }

        private void FillReceiveBillEntryBankInfo(FormMetadata targetBillMeta, DynamicObject receiveBill, string entryPropertyName, Dictionary<long, ClaimFlowBankInfo> flowMap)
        {
            DynamicObjectCollection entries = GetDynamicObjectCollection(receiveBill, entryPropertyName);
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            foreach (DynamicObject entry in entries)
            {
                ClaimFlowBankInfo flowInfo = GetFlowInfoForReceiveEntry(entry, flowMap);
                if (flowInfo == null)
                {
                    continue;
                }

                SetDynamicBaseDataValueIfExists(targetBillMeta, entry, "ACCOUNTID", flowInfo.AccountId);
                SetBaseDataValueIfFieldExists(targetBillMeta, entry, "ACCOUNTID", flowInfo.AccountId);
                SetDynamicValueIfExists(entry, "ACCOUNTID_Id", flowInfo.AccountId);
                SetBaseDataRefIdIfFieldExists(targetBillMeta, entry, "FACCOUNTID", flowInfo.AccountId);
                SetBaseDataRefIdIfFieldExists(targetBillMeta, entry, "FACCOUNT", flowInfo.AccountId);
                SetBaseDataRefIdIfFieldExists(targetBillMeta, entry, "FRECEIVEACCOUNTID", flowInfo.AccountId);
                SetBaseDataRefIdIfFieldExists(targetBillMeta, entry, "FBANKACCOUNTID", flowInfo.AccountId);
                SetBaseDataRefIdIfFieldExists(targetBillMeta, entry, "FOPPOSITEBANKACCOUNTID", flowInfo.AccountId);
                SetDynamicValueIfExists(entry, "FOPPOSITEBANKACCOUNT", flowInfo.OppositeBankAccount);
                SetDynamicValueIfExists(entry, "FOPPOSITEBANKNAME", flowInfo.OppositeBankName);
                SetDynamicValueIfExists(entry, "FOPPOSITECCOUNTNAME", flowInfo.OppositeAccountName);
            }
        }

        private int GetInnerAccountId(ClaimFlowBankInfo flowInfo)
        {
            if (flowInfo == null || flowInfo.AccountId <= 0)
            {
                return 0;
            }

            if (flowInfo.InnerAccountId > 0)
            {
                return flowInfo.InnerAccountId;
            }

            string sql = string.Format(@"
SELECT TOP 1 ia.FID
FROM T_CN_INNERACCOUNT ia
LEFT JOIN T_CN_INNERACCOUNT_L ial ON ia.FID = ial.FID AND ial.FLOCALEID = 2052
WHERE ia.FDOCUMENTSTATUS = 'C'
  AND ia.FFORBIDSTATUS = 'A'
  AND (
      ia.FNUMBER = N'{0}'
      OR ial.FNAME = N'{1}'
      OR ia.FID = {2}
  )
ORDER BY ia.FID", SqlSafe(flowInfo.AccountNumber), SqlSafe(flowInfo.AccountName), flowInfo.AccountId.ToString(CultureInfo.InvariantCulture));

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                if (reader.Read())
                {
                    flowInfo.InnerAccountId = Convert.ToInt32(reader["FID"]);
                    return flowInfo.InnerAccountId;
                }
            }

            throw new Exception($"未找到源银行账号对应的内部账号：银行账号ID={flowInfo.AccountId}，编码={flowInfo.AccountNumber}，名称={flowInfo.AccountName}；{BuildAccountMappingDiagnostic(flowInfo.AccountId)}");
        }

        private string BuildAccountMappingDiagnostic(int accountId)
        {
            var parts = new List<string>();
            string sql = string.Format(@"
SELECT TOP 20 t.name AS TableName, c.name AS ColumnName
FROM sys.tables t
INNER JOIN sys.columns c ON t.object_id = c.object_id
WHERE c.name IN ('FID', 'FACCOUNTID', 'FACCTID', 'FACCOUNTID', 'FINNERACCOUNTID')
  AND (t.name LIKE 'T_CN_%ACCOUNT%' OR t.name LIKE 'T_BD_%ACCOUNT%' OR t.name LIKE 'T_BD_%BANK%')
ORDER BY t.name, c.name");

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                var columns = new List<string>();
                while (reader.Read())
                {
                    columns.Add(Convert.ToString(reader["TableName"]) + "." + Convert.ToString(reader["ColumnName"]));
                }

                parts.Add("账号相关表字段=" + string.Join(",", columns));
            }

            string innerSql = string.Format(@"
SELECT TOP 5 FID, FNUMBER
FROM T_CN_INNERACCOUNT
WHERE FFORBIDSTATUS = 'A'
ORDER BY FID");

            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, innerSql))
            {
                var innerAccounts = new List<string>();
                while (reader.Read())
                {
                    innerAccounts.Add("FID=" + Convert.ToString(reader["FID"]) + ",FNUMBER=" + Convert.ToString(reader["FNUMBER"]));
                }

                parts.Add("内部账号样例=" + string.Join("|", innerAccounts));
            }

            string cashAccountSql = string.Format(@"
SELECT TOP 5 FID, FNUMBER
FROM T_CN_CASHACCOUNT
WHERE FID = {0}
ORDER BY FID", accountId);
            AppendSimpleRows(parts, "现金账号命中", cashAccountSql, new[] { "FID", "FNUMBER" });

            string cashAccountSampleSql = @"
SELECT TOP 5 FID, FNUMBER
FROM T_CN_CASHACCOUNT
ORDER BY FID";
            AppendSimpleRows(parts, "现金账号样例", cashAccountSampleSql, new[] { "FID", "FNUMBER" });

            string bankToAccountEntrySql = string.Format(@"
SELECT TOP 5 FID, FACCOUNTID
FROM T_CN_BANKTOACCOUNTENTRY
WHERE FACCOUNTID = {0} OR FID = {0}
ORDER BY FID", accountId);
            AppendSimpleRows(parts, "银企账号分录命中", bankToAccountEntrySql, new[] { "FID", "FACCOUNTID" });

            string entryAccountSql = string.Format(@"
SELECT TOP 5 FID, FNUMBER
FROM T_CN_ENTRYACCOUNT
WHERE FID = {0}
ORDER BY FID", accountId);
            AppendSimpleRows(parts, "登账账号命中", entryAccountSql, new[] { "FID", "FNUMBER" });

            return string.Join("；", parts);
        }

        private void AppendSimpleRows(List<string> parts, string title, string sql, string[] fields)
        {
            using (IDataReader reader = DBUtils.ExecuteReader(this.Context, sql))
            {
                var rows = new List<string>();
                while (reader.Read())
                {
                    var values = new List<string>();
                    foreach (string field in fields)
                    {
                        values.Add(field + "=" + Convert.ToString(reader[field]));
                    }

                    rows.Add(string.Join(",", values));
                }

                parts.Add(title + "=" + string.Join("|", rows));
            }
        }

        private string SqlSafe(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private string BuildReceiveBillBankValueDiagnostic(DynamicObject[] receiveBills)
        {
            if (receiveBills == null || receiveBills.Length == 0)
            {
                return "无目标收款单对象";
            }

            string[] entryNames = { "RECEIVEBILLENTRY", "RECEIVEBILLREC", "RECEIVEBILLSRCENTRY" };
            string[] fieldNames = { "ACCOUNTID_Id", "ACCOUNTID", "FINNERACCOUNTID_Id", "FINNERACCOUNTID", "FInnerAccountID_B_Id", "FInnerAccountID_B", "CashAccount_Id", "CashAccount", "SETTLETYPEID_Id", "SETTLETYPEID" };
            var parts = new List<string>();
            foreach (string entryName in entryNames)
            {
                DynamicObjectCollection entries = GetDynamicObjectCollection(receiveBills[0], entryName);
                if (entries == null)
                {
                    parts.Add(entryName + "=不存在");
                    continue;
                }

                if (entries.Count == 0)
                {
                    parts.Add(entryName + "=空集合");
                    continue;
                }

                var values = new List<string>();
                foreach (string fieldName in fieldNames)
                {
                    if (!entries[0].DynamicObjectType.Properties.ContainsKey(fieldName))
                    {
                        continue;
                    }

                    values.Add(fieldName + "=" + FormatDiagnosticValue(entries[0][fieldName]));
                }

                parts.Add(entryName + "[0]=" + string.Join(",", values));
            }

            return string.Join("；", parts);
        }

        private string FormatDiagnosticValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            DynamicObject dynamicObject = value as DynamicObject;
            if (dynamicObject != null)
            {
                var values = new List<string>();
                string[] names = { "Id", "Number", "Name" };
                foreach (string name in names)
                {
                    if (dynamicObject.DynamicObjectType.Properties.ContainsKey(name))
                    {
                        values.Add(name + ":" + Convert.ToString(dynamicObject[name]));
                    }
                }

                return "{" + string.Join("|", values) + "}";
            }

            return Convert.ToString(value);
        }

        private string BuildReceiveBillEntryPropertyDiagnostic(DynamicObject[] receiveBills)
        {
            if (receiveBills == null || receiveBills.Length == 0)
            {
                return "无目标收款单对象";
            }

            string[] entryNames = { "RECEIVEBILLENTRY", "RECEIVEBILLREC", "RECEIVEBILLSRCENTRY" };
            var parts = new List<string>();
            foreach (string entryName in entryNames)
            {
                DynamicObjectCollection entries = GetDynamicObjectCollection(receiveBills[0], entryName);
                if (entries == null)
                {
                    parts.Add(entryName + "=不存在");
                    continue;
                }

                if (entries.Count == 0)
                {
                    parts.Add(entryName + "=空集合");
                    continue;
                }

                var propertyNames = new List<string>();
                foreach (var property in entries[0].DynamicObjectType.Properties)
                {
                    propertyNames.Add(property.Name);
                }

                string properties = string.Join(" ", propertyNames);
                parts.Add(entryName + "[0]=" + properties);
            }

            return string.Join("；", parts);
        }

        private DynamicObjectCollection GetDynamicObjectCollection(DynamicObject data, string propertyName)
        {
            if (data == null || !data.DynamicObjectType.Properties.ContainsKey(propertyName))
            {
                return null;
            }

            return data[propertyName] as DynamicObjectCollection;
        }

        private ClaimFlowBankInfo GetFlowInfoForReceiveEntry(DynamicObject entry, Dictionary<long, ClaimFlowBankInfo> flowMap)
        {
            long sourceBillId = GetDynamicLongValue(entry, "FSRCBILLID");
            if (sourceBillId <= 0)
            {
                sourceBillId = GetDynamicLongValue(entry, "SRCBILLID");
            }

            ClaimFlowBankInfo flowInfo;
            if (sourceBillId > 0 && flowMap.TryGetValue(sourceBillId, out flowInfo))
            {
                return flowInfo;
            }

            return flowMap.Values.FirstOrDefault(o => o != null && o.AccountId > 0);
        }

        private long GetDynamicLongValue(DynamicObject data, string propertyName)
        {
            if (data == null || !data.DynamicObjectType.Properties.ContainsKey(propertyName))
            {
                return 0;
            }

            object value = data[propertyName];
            if (value == null)
            {
                return 0;
            }

            long result;
            if (long.TryParse(Convert.ToString(value), out result))
            {
                return result;
            }

            return 0;
        }

        private bool SetDynamicBaseDataValueIfExists(FormMetadata meta, DynamicObject data, string propertyName, int refId)
        {
            if (meta == null || data == null || refId <= 0 || !data.DynamicObjectType.Properties.ContainsKey(propertyName))
            {
                return false;
            }

            BaseDataField field = FindBaseDataFieldByDynamicPropertyName(meta, propertyName);
            if (field == null || field.DynamicProperty == null || field.RefIDDynamicProperty == null)
            {
                return false;
            }

            DynamicObject baseData = BusinessDataServiceHelper.LoadSingle(this.Context, refId, field.RefFormDynamicObjectType);
            if (baseData == null)
            {
                return false;
            }

            field.DynamicProperty.SetValue(data, baseData);
            field.RefIDDynamicProperty.SetValue(data, refId);
            return true;
        }

        private BaseDataField FindBaseDataFieldByDynamicPropertyName(FormMetadata meta, string propertyName)
        {
            foreach (var field in meta.BusinessInfo.GetFieldList())
            {
                BaseDataField baseDataField = field as BaseDataField;
                if (baseDataField == null || baseDataField.DynamicProperty == null)
                {
                    continue;
                }

                if (string.Equals(baseDataField.DynamicProperty.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return baseDataField;
                }
            }

            return null;
        }

        private bool SetBaseDataValueIfFieldExists(FormMetadata meta, DynamicObject data, string fieldKey, int refId)
        {
            if (meta == null || data == null || refId <= 0)
            {
                return false;
            }

            BaseDataField field = meta.BusinessInfo.GetField(fieldKey) as BaseDataField;
            if (field == null || field.DynamicProperty == null || field.RefIDDynamicProperty == null)
            {
                return false;
            }

            if (!data.DynamicObjectType.Properties.ContainsKey(field.DynamicProperty.Name) || !data.DynamicObjectType.Properties.ContainsKey(field.RefIDDynamicProperty.Name))
            {
                return false;
            }

            DynamicObject baseData = BusinessDataServiceHelper.LoadSingle(this.Context, refId, field.RefFormDynamicObjectType);
            if (baseData == null)
            {
                return false;
            }

            field.DynamicProperty.SetValue(data, baseData);
            field.RefIDDynamicProperty.SetValue(data, refId);
            return true;
        }

        private bool SetBaseDataRefIdIfFieldExists(FormMetadata meta, DynamicObject data, string fieldKey, int refId)
        {
            if (meta == null || data == null || refId <= 0)
            {
                return false;
            }

            BaseDataField field = meta.BusinessInfo.GetField(fieldKey) as BaseDataField;
            if (field == null || field.RefIDDynamicProperty == null)
            {
                return false;
            }

            if (!data.DynamicObjectType.Properties.ContainsKey(field.RefIDDynamicProperty.Name))
            {
                return false;
            }

            object oldValue = field.RefIDDynamicProperty.GetValue(data);
            int oldId = oldValue == null ? 0 : Convert.ToInt32(oldValue);
            if (oldId == refId)
            {
                return false;
            }

            field.RefIDDynamicProperty.SetValue(data, refId);
            return true;
        }

        private sealed class ClaimFlowBankInfo
        {
            public int SettleTypeId { get; set; }

            public int AccountId { get; set; }

            public string AccountNumber { get; set; }

            public string AccountName { get; set; }

            public int InnerAccountId { get; set; }

            public string OppositeBankAccount { get; set; }

            public string OppositeBankName { get; set; }

            public string OppositeAccountName { get; set; }
        }

        private sealed class BatchKey
        {
            public decimal RecAmount { get; set; }
            public int PayUnit { get; set; }
            public int RecOrgId { get; set; }
            public int AccountId { get; set; }
            public int SettleTypeId { get; set; }

            public string ToToken()
            {
                return string.Join("|",
                    RecAmount.ToString("G29", CultureInfo.InvariantCulture),
                    PayUnit.ToString(CultureInfo.InvariantCulture),
                    RecOrgId.ToString(CultureInfo.InvariantCulture),
                    AccountId.ToString(CultureInfo.InvariantCulture),
                    SettleTypeId.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 执行单据下推，生成收款单（合并传入的全部认领单）
        /// </summary>
        private void PushToReceiveBill(List<long> billIds, string sourceFormId)
        {
            try
            {
                var ruleMeta = ConvertServiceHelper.GetConvertRule(this.Context, CONVERT_RULE_ID);
                if (ruleMeta == null)
                {
                    throw new Exception("未找到收款认领单到收款单的转换规则，请检查转换规则是否启用！");
                }

                var rule = ruleMeta.Rule;

                List<ListSelectedRow> selectedRows = new List<ListSelectedRow>();
                foreach (long billId in billIds)
                {
                    ListSelectedRow row = new ListSelectedRow(Convert.ToString(billId), string.Empty, 0, sourceFormId);
                    selectedRows.Add(row);
                }

                PushArgs pushArgs = new PushArgs(rule, selectedRows.ToArray())
                {
                    TargetBillTypeId = "",
                    TargetOrgId = 0,
                    CustomParams = new Dictionary<string, object>()
                };

                ConvertOperationResult operationResult = ConvertServiceHelper.Push(this.Context, pushArgs, OperateOption.Create());

                if (!operationResult.IsSuccess)
                {
                    var errorMsg = GetOperationErrorMessage(operationResult, "下推失败");
                    throw new Exception($"下推失败：{errorMsg}");
                }

                DynamicObject[] objs = (from p in operationResult.TargetDataEntities
                                        select p.DataEntity).ToArray();

                if (objs == null || objs.Length == 0)
                {
                    throw new Exception("下推生成的目标单据为空！");
                }

                var targetBillMeta = MetaDataServiceHelper.Load(this.Context, TARGET_FORMID) as FormMetadata;
                OperateOption saveOption = OperateOption.Create();
                saveOption.SetIgnoreWarning(true);

                FillReceiveBillBankInfo(objs, billIds, targetBillMeta);

                var saveResult = BusinessDataServiceHelper.Save(this.Context, targetBillMeta.BusinessInfo, objs, saveOption, "Save");

                if (!saveResult.IsSuccess)
                {
                    var errorMsg = GetOperationErrorMessage(saveResult, "保存失败");
                    throw new Exception($"保存收款单失败：{errorMsg}；银行字段赋值诊断：{BuildReceiveBillBankValueDiagnostic(objs)}");
                }

                object[] savedBillIds = new object[objs.Length];
                for (int i = 0; i < objs.Length; i++)
                {
                    savedBillIds[i] = objs[i][0];
                }

                var submitResult = BusinessDataServiceHelper.Submit(this.Context, targetBillMeta.BusinessInfo, savedBillIds, "Submit", saveOption);
                if (!submitResult.IsSuccess)
                {
                    var errorMsg = GetOperationErrorMessage(submitResult, "提交失败");
                    throw new Exception($"提交收款单失败：{errorMsg}");
                }

                var applyResult = BusinessDataServiceHelper.Audit(this.Context, targetBillMeta.BusinessInfo, savedBillIds, saveOption);
                if (!applyResult.IsSuccess)
                {
                    var errorMsg = GetOperationErrorMessage(applyResult, "审核失败");
                    throw new Exception($"审核收款单失败：{errorMsg}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"下推收款单操作失败：{ex.Message}");
            }
        }
    }
}
