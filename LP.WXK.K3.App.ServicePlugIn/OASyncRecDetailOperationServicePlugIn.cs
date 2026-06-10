using Kingdee.BOS.Core.DynamicForm.PlugIn;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.App.Data;
using LP.WXK.K3.App.RecDetailSyncSchedule;
using System.Data;
using Kingdee.BOS;
using Kingdee.BOS.Util;

namespace LP.WXK.K3.App.ServicePlugIn
{
    [Description("【操作插件】测试:ERP银行交易明细传输至OA回款明细（需设置过滤条件或人工选择标记）"), HotUpdate]
    public class OASyncRecDetailOperationServicePlugIn : AbstractOperationServicePlugIn
    {

        /// <summary>
        /// 允许推送OA回款明细的组织编码（FNUMBER）列表
        /// 102 洋浦京泰药业有限公司、104 内蒙古白医制药股份有限公司、108 西藏中卫诚康药业有限公司
        /// </summary>
        private static readonly HashSet<string> ALLOWED_SETTLE_ORG_NUMBERS = new HashSet<string> { "102", "104", "108" };

        // 单据保存成功后,同步OA
        public override void AfterExecuteOperationTransaction(AfterExecuteOperationTransaction e)
        {
            base.AfterExecuteOperationTransaction(e);
            RecDetailService recDetailSync = new RecDetailService();

            int syncSuccessCount = 0;
            int syncFailCount = 0;

            foreach (DynamicObject entity in e.DataEntitys)
            {
                if (entity != null)
                {
                    string billNo = Convert.ToString(entity["BillNo"]);
                    long billId = Convert.ToInt64(entity["Id"]);
                    var tableName = "T_CN_BANKCASHFLOW";

                    // 检查结算组织是否在允许推送的范围内（通过组织编码FNUMBER判断）
                    string settleOrgNumber = GetSettleOrgNumber(this.Context, billId);
                    if (!ALLOWED_SETTLE_ORG_NUMBERS.Contains(settleOrgNumber))
                    {
                        throw new Exception($"银行交易明细 {billNo} 的结算组织（编码={settleOrgNumber}）不在允许推送OA的范围内！");
                    }

                    // 检查贷方金额是否大于0
                    decimal creditAmount = GetCreditAmount(this.Context, billId);
                    if (creditAmount <= 0)
                    {
                        throw new Exception($"银行交易明细 {billNo} 贷方金额为0，不允许推送OA！");
                    }

                    // 检查交易流水号是否已关联收款认领单
                    string bankSeqNo = GetBankSeqNo(this.Context, billId);
                    if (IsBankSeqNoAssociated(this.Context, bankSeqNo))
                    {
                        throw new Exception($"银行交易明细 {billNo} 的交易流水号 {bankSeqNo} 已关联收款认领单，不允许重复推送！");
                    }

                    // 检查是否已同步成功
                    if (IsAlreadySynced(this.Context, tableName, billNo))
                    {
                        throw new Exception("单据已成功同步OA，不需要再次同步");
                    }

                    bool isSync = recDetailSync.syncBill(this.Context, billNo);
                    // F_TWLG_OAStatus = 0（未反写）、1（已处理）
                    if (isSync)
                    {
                        string sqlStr = string.Format(@"update {0} set F_TWLG_OAStatus = 1 where FBILLNO = '{1}'", tableName, billNo);
                        DBUtils.Execute(this.Context, sqlStr);
                        syncSuccessCount++;
                    }
                    else
                    {
                        string sqlStr = string.Format(@"update {0} set F_TWLG_OAStatus = 2 where FBILLNO = '{1}'", tableName, billNo);
                        DBUtils.Execute(this.Context, sqlStr);
                        syncFailCount++;
                    }
                }
            }

            // 记录同步结果日志
            string logMessage = syncFailCount > 0
                ? $"银行交易明细OA同步完成：成功 {syncSuccessCount} 笔，失败 {syncFailCount} 笔"
                : $"银行交易明细OA同步完成：成功 {syncSuccessCount} 笔";

            Kingdee.BOS.Log.Logger.Info("OASyncRecDetail", logMessage);
        }

        /// <summary>
        /// 检查单据是否已同步成功
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="tableName">表名</param>
        /// <param name="billNo">单据编号</param>
        /// <returns>是否已同步成功</returns>
        private bool IsAlreadySynced(Context ctx, string tableName, string billNo)
        {
            try
            {
                string sql = string.Format("SELECT F_TWLG_OAStatus FROM {0} WHERE FBILLNO = '{1}'", tableName, billNo);
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        object status = reader["F_TWLG_OAStatus"];
                        if (status != null && status != DBNull.Value)
                        {
                            return Convert.ToInt32(status) == 1;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// 获取结算组织的编码（FNUMBER）
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="billId">单据ID</param>
        /// <returns>结算组织编码（FNUMBER），未找到返回空字符串</returns>
        private string GetSettleOrgNumber(Context ctx, long billId)
        {
            try
            {
                string sql = string.Format(@"
                    SELECT o.FNUMBER
                    FROM T_CN_BANKCASHFLOW h
                    INNER JOIN T_ORG_Organizations o ON h.FSETTLEORGID = o.FORGID
                    WHERE h.FID = {0}
                      AND o.FDOCUMENTSTATUS = 'C'
                      AND o.FFORBIDSTATUS = 'A'", billId);
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToString(reader["FNUMBER"]) ?? "";
                    }
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        /// <summary>
        /// 获取贷方金额
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="billId">单据ID</param>
        /// <returns>贷方金额</returns>
        private decimal GetCreditAmount(Context ctx, long billId)
        {
            try
            {
                string sql = string.Format("SELECT FCREDITAMOUNT FROM T_CN_BANKCASHFLOW WHERE FID = {0}", billId);
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToDecimal(reader["FCREDITAMOUNT"]);
                    }
                }
            }
            catch (Exception)
            {
            }
            return 0;
        }

        /// <summary>
        /// 获取交易流水号
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="billId">单据ID</param>
        /// <returns>交易流水号</returns>
        private string GetBankSeqNo(Context ctx, long billId)
        {
            try
            {
                string sql = string.Format("SELECT FSETTLENO FROM T_CN_BANKCASHFLOW WHERE FID = {0}", billId);
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        return Convert.ToString(reader["FSETTLENO"]) ?? "";
                    }
                }
            }
            catch (Exception)
            {
            }
            return "";
        }

        /// <summary>
        /// 检查交易流水号是否已关联收款认领单
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="bankSeqNo">交易流水号</param>
        /// <returns>是否已关联</returns>
        private bool IsBankSeqNoAssociated(Context ctx, string bankSeqNo)
        {
            if (string.IsNullOrWhiteSpace(bankSeqNo))
            {
                return false;
            }
            try
            {
                string sql = string.Format(@"
                    SELECT 1 FROM T_CN_RECCLAIMBILLENTRY e
                    INNER JOIN T_CN_RECCLAIMBILL c ON e.FID = c.FID
                    WHERE e.FBNKSEQNO = '{0}' AND c.FDOCUMENTSTATUS = 'C'",
                    bankSeqNo.Replace("'", "''"));
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    return reader.Read();
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
    }
}
