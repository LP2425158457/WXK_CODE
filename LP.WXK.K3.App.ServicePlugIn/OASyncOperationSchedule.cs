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

        public void Run(Context ctx, Schedule schedule)
        {
            ProcessPayBill(ctx);
            ProcessRefundBill(ctx);
        }

        private void ProcessPayBill(Context ctx)
        {
            OASyncService oASync = new OASyncService(ctx, true);

            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AP_PAYBILL a
                INNER JOIN T_AP_PAYBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBANKSTATUS = '{0}'";

            sql = string.Format(sql, BANK_STATUS_PAID);
            ProcessBills(ctx, oASync, sql, "T_AP_PAYBILL");
        }

        private void ProcessRefundBill(Context ctx)
        {
            OASyncService oASync = new OASyncService(ctx, false);

            string sql = @"
                SELECT DISTINCT a.FID, a.F_TWLG_OAPROCESSID
                FROM T_AR_REFUNDBILL a
                INNER JOIN T_AR_REFUNDBILLENTRY_B b ON a.FID = b.FID
                WHERE (a.F_TWLG_OAStatus = 0 OR a.F_TWLG_OAStatus = 2 OR a.F_TWLG_OAStatus IS NULL)
                  AND b.FBankStatus = '{0}'";

            sql = string.Format(sql, BANK_STATUS_PAID);
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
