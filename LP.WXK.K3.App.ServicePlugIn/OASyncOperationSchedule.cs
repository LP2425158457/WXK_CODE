using Kingdee.BOS;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Core;
using Kingdee.BOS.App.Data;
using System;
using System.Data;

namespace LP.WXK.K3.App.ServicePlugIn
{
    public class OASyncOperationSchedule : IScheduleService
    {
        private const string BANK_STATUS_PAID = "F";
        private const string BANK_STATUS_BANK_SUCCESS = "C";

        public void Run(Context ctx, Schedule schedule)
        {
            // 从定时任务参数中获取推送起始时间
            string pushStartTime = schedule.Parameters;

            ProcessPayBill(ctx, pushStartTime);
            ProcessRefundBill(ctx, pushStartTime);
        }

        private void ProcessPayBill(Context ctx, string pushStartTime)
        {
            OASyncService oASync = new OASyncService(true);

            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AP_PAYBILL a
                INNER JOIN T_AP_PAYBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBANKSTATUS = '{0}'";

            sql = string.Format(sql, BANK_STATUS_PAID);

            // 如果指定了推送起始时间，则只推送该时间之后的数据
            if (!string.IsNullOrWhiteSpace(pushStartTime))
            {
                DateTime filterDate;
                if (DateTime.TryParse(pushStartTime, out filterDate))
                {
                    string filterDateStr = filterDate.ToString("yyyy-MM-dd");
                    sql += string.Format(" AND a.FDATE >= '{0}'", filterDateStr);
                }
            }

            ProcessBills(ctx, oASync, sql, "T_AP_PAYBILL");
        }

        private void ProcessRefundBill(Context ctx, string pushStartTime)
        {
            OASyncService oASync = new OASyncService(false);

            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AR_REFUNDBILL a
                INNER JOIN T_AR_REFUNDBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBankStatus IN ('{0}', '{1}')";

            sql = string.Format(sql, BANK_STATUS_PAID, BANK_STATUS_BANK_SUCCESS);

            // 如果指定了推送起始时间，则只推送该时间之后的数据
            if (!string.IsNullOrWhiteSpace(pushStartTime))
            {
                DateTime filterDate;
                if (DateTime.TryParse(pushStartTime, out filterDate))
                {
                    string filterDateStr = filterDate.ToString("yyyy-MM-dd");
                    sql += string.Format(" AND a.FDATE >= '{0}'", filterDateStr);
                }
            }

            ProcessBills(ctx, oASync, sql, "T_AR_REFUNDBILL");
        }

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
