using Kingdee.BOS;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Core;
using Kingdee.BOS.App.Data;
using System;
using System.Data;

namespace LP.WXK.K3.App.ServicePlugIn
{
    /// <summary>
    /// OA同步定时任务
    /// 定时查询"未反写OA状态"且"银行处理状态=已付款确认"的单据，推送到OA系统
    /// </summary>
    public class OASyncOperationSchedule : IScheduleService
    {
        /// <summary>
        /// 银行处理状态：已付款
        /// </summary>
        private const string BANK_STATUS_PAID = "F";

        /// <summary>
        /// 定时任务执行入口
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="schedule">定时任务配置</param>
        public void Run(Context ctx, Schedule schedule)
        {
            OASyncService oASync = new OASyncService();

            ProcessPayBill(ctx, oASync);
            ProcessRefundBill(ctx, oASync);
        }

        /// <summary>
        /// 处理付款单
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="oASync">OA同步服务</param>
        private void ProcessPayBill(Context ctx, OASyncService oASync)
        {
            // 查询条件：银行处理状态=已付款确认
            // 状态：0-未同步，1-已同步，2-同步失败，3-已排除
            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AP_PAYBILL a
                INNER JOIN T_AP_PAYBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBANKSTATUS = '{0}'";

            sql = string.Format(sql, BANK_STATUS_PAID);
            ProcessBills(ctx, oASync, sql, "T_AP_PAYBILL");
        }

        /// <summary>
        /// 处理收款退款单
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="oASync">OA同步服务</param>
        private void ProcessRefundBill(Context ctx, OASyncService oASync)
        {
            // 查询条件：银行处理状态=已付款确认
            // 状态：0-未同步，1-已同步，2-同步失败，3-已排除
            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AR_REFUNDBILL a
                INNER JOIN T_AR_REFUNDBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBankStatus = '{0}'";

            sql = string.Format(sql, BANK_STATUS_PAID);
            ProcessBills(ctx, oASync, sql, "T_AR_REFUNDBILL");
        }

        /// <summary>
        /// 处理单据通用方法
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="oASync">OA同步服务</param>
        /// <param name="sql">查询SQL</param>
        /// <param name="tableName">表名</param>
        private void ProcessBills(Context ctx, OASyncService oASync, string sql, string tableName)
        {
            try
            {
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    while (reader.Read())
                    {
                        long billId = Convert.ToInt64(reader["FID"]);
                        string oaprocessid = Convert.ToString(reader["F_TWLG_OAPROCESSID"]);
                        try
                        {

                            // 检查流程编码是否为空
                            if (string.IsNullOrWhiteSpace(oaprocessid))
                            {
                                continue;
                            }

                            bool isSync = oASync.skipCurrentCodeAsync(ctx, oaprocessid);

                            string updateSql;
                            if (isSync)
                            {
                                updateSql = string.Format(
                                    "UPDATE {0} SET F_TWLG_OAStatus = 1 WHERE FID = {1}",
                                    tableName, billId);
                            }
                            else
                            {
                                updateSql = string.Format(
                                    "UPDATE {0} SET F_TWLG_OAStatus = 2 WHERE FID = {1}",
                                    tableName, billId);
                            }
                            DBUtils.Execute(ctx, updateSql);
                        }
                        catch (Exception)
                        {
                            string updateSql = string.Format(
                                "UPDATE {0} SET F_TWLG_OAStatus = 2 WHERE FID = {1}",
                                tableName, billId);
                            DBUtils.Execute(ctx, updateSql);
                            continue;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
