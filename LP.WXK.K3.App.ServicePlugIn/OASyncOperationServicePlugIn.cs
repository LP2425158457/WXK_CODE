using Kingdee.BOS.Core.DynamicForm.PlugIn;
using System.ComponentModel;
using Kingdee.BOS.Util;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.App.Data;
using Kingdee.BOS.Orm.DataEntity;
using System;
using System.Data;
using Kingdee.BOS;

namespace LP.WXK.K3.App.ServicePlugIn
{
    [Description("【操作插件】付款单、收款退款单的\"已付款确认\"增加插件，调用OA同步；并写入日志；"), HotUpdate]
    public class OASyncOperationServicePlugIn : AbstractOperationServicePlugIn
    {

        public override void AfterExecuteOperationTransaction(AfterExecuteOperationTransaction e)
        {
            base.AfterExecuteOperationTransaction(e);

            int syncSuccessCount = 0;
            int syncFailCount = 0;

            foreach (DynamicObject entity in e.DataEntitys)
            {
                if (entity != null)
                {
                    long payId = Convert.ToInt64(entity["Id"]);
                    string billNo = Convert.ToString(entity["BillNo"]);
                    var typeName = entity.DynamicObjectType.Name;
                    var tableName = "";
                    string oaprocessid = "";
                    bool isPayBill = false;

                    if (typeName.Equals("PAYBILL"))
                    {
                        tableName = "T_AP_PAYBILL";
                        isPayBill = true;
                    }
                    else if (typeName.Equals("REFUNDBILL"))
                    {
                        tableName = "T_AR_REFUNDBILL";

                        if (!IsBankStatusPaid(this.Context, payId))
                        {
                           throw new Exception($"收款退款单 {billNo} 尚未执行已付款确认，不允许同步OA！");
                        }
                    }

                    OASyncService oASync = new OASyncService(this.Context, isPayBill);

                    oaprocessid = GetOAProcessId(this.Context, tableName, payId);

                    if (IsAlreadySynced(this.Context, tableName, payId))
                    {
                        throw new Exception("单据已成功同步OA，不需要再次同步");
                    }

                    if (string.IsNullOrWhiteSpace(oaprocessid))
                    {
                       throw new Exception($"所选单据：{billNo} 不存在OA流程ID，不允许推送！");
                    }

                    bool isSync = oASync.skipCurrentCodeAsync(this.Context, oaprocessid);
                    if (isSync)
                    {
                        string sqlStr = string.Format(@"update {0} set F_TWLG_OAStatus = 1 where FID = {1}", tableName, payId);
                        DBUtils.Execute(this.Context, sqlStr);
                        syncSuccessCount++;
                    }
                    else
                    {
                        string sqlStr = string.Format(@"update {0} set F_TWLG_OAStatus = 2 where FID = {1}", tableName, payId);
                        DBUtils.Execute(this.Context, sqlStr);
                        syncFailCount++;
                    }
                }
            }

            string logMessage = syncFailCount > 0
                ? $"OA同步完成：成功 {syncSuccessCount} 笔，失败 {syncFailCount} 笔"
                : $"OA同步完成：成功 {syncSuccessCount} 笔";

            Kingdee.BOS.Log.Logger.Info("OASync", logMessage);
        }

        private bool IsAlreadySynced(Context ctx, string tableName, long billId)
        {
            try
            {
                string sql = string.Format("SELECT F_TWLG_OAStatus FROM {0} WHERE FID = {1}", tableName, billId);
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

        private string GetOAProcessId(Context ctx, string tableName, long billId)
        {
            string oaprocessid = "";
            try
            {
                string sql = string.Format("SELECT F_TWLG_OAPROCESSID FROM {0} WHERE FID = {1}", tableName, billId);
                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        oaprocessid = Convert.ToString(reader["F_TWLG_OAPROCESSID"]) ?? "";
                    }
                }
            }
            catch (Exception)
            {
            }
            return oaprocessid;
        }

        private bool IsBankStatusPaid(Context ctx, long billId)
        {
            try
            {
                string sql = string.Format(@"
                    SELECT FBankStatus 
                    FROM T_AR_REFUNDBILLENTRY_B 
                    WHERE FID = {0}", billId);

                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        string bankStatus = Convert.ToString(reader["FBankStatus"]);
                        return bankStatus == "F";
                    }
                }
            }
            catch (Exception)
            {
                try
                {
                    string sql = string.Format(@"
                        SELECT FBANKSTATUS 
                        FROM T_AR_REFUNDBILLENTRY_B 
                        WHERE FID = {0}", billId);

                    using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                    {
                        if (reader.Read())
                        {
                            string bankStatus = Convert.ToString(reader["FBANKSTATUS"]);
                            return bankStatus == "F";
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return false;
        }
    }
}
