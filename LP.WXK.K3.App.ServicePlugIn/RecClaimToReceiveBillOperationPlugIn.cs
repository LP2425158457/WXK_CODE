using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Core.Metadata;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.App.Data;
using Kingdee.BOS.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using Kingdee.BOS.Core.DynamicForm.PlugIn;
using Kingdee.BOS.Core.DynamicForm.Operation;
using System.Data;
using Kingdee.BOS.Core.List;
using Kingdee.BOS.Core.Metadata.ConvertElement.ServiceArgs;
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

        public override void AfterExecuteOperationTransaction(AfterExecuteOperationTransaction e)
        {
            base.AfterExecuteOperationTransaction(e);

            if (e.DataEntitys == null || e.DataEntitys.Length == 0)
            {
                return;
            }

            if (!IsSaveOperation())
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
                    var msg = submitResult.OperateResult.FirstOrDefault()?.Message ?? "提交失败";
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
                var msg = auditResult.OperateResult.FirstOrDefault()?.Message ?? "审核失败";
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
                    var errorMsg = operationResult.OperateResult.FirstOrDefault()?.Message ?? "下推失败";
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

                var saveResult = BusinessDataServiceHelper.Save(this.Context, targetBillMeta.BusinessInfo, objs, saveOption, "Audit");

                if (!saveResult.IsSuccess)
                {
                    var errorMsg = saveResult.OperateResult.FirstOrDefault()?.Message ?? "保存失败";
                    throw new Exception($"保存收款单失败：{errorMsg}");
                }

                object[] savedBillIds = new object[objs.Length];
                for (int i = 0; i < objs.Length; i++)
                {
                    savedBillIds[i] = objs[i][0];
                }

                var submitResult = BusinessDataServiceHelper.Submit(this.Context, targetBillMeta.BusinessInfo, savedBillIds, "Submit", saveOption);
                if (!submitResult.IsSuccess)
                {
                    var errorMsg = submitResult.OperateResult.FirstOrDefault()?.Message ?? "提交失败";
                    throw new Exception($"提交收款单失败：{errorMsg}");
                }

                var applyResult = BusinessDataServiceHelper.Audit(this.Context, targetBillMeta.BusinessInfo, savedBillIds, saveOption);
                if (!applyResult.IsSuccess)
                {
                    var errorMsg = applyResult.OperateResult.FirstOrDefault()?.Message ?? "审核失败";
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
