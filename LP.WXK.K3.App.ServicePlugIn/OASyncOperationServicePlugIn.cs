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
    [Description("【操作插件】付款单、收款退款单的“已付款确认”增加插件，调用OA同步；并写入日志；"), HotUpdate]
    public class OASyncOperationServicePlugIn : AbstractOperationServicePlugIn
    {

        public override void AfterExecuteOperationTransaction(AfterExecuteOperationTransaction e)
        {
            base.AfterExecuteOperationTransaction(e);

            OASyncService oASync = new OASyncService();
            int syncSuccessCount = 0;
            int syncFailCount = 0;

            // 读取全部的单据,for循环,转换成DynamicObject类型
            foreach (DynamicObject entity in e.DataEntitys)
            {
                // 如果不为空,开始循环
                if (entity != null)
                {
                    long payId = Convert.ToInt64(entity["Id"]);
                    string billNo = Convert.ToString(entity["BillNo"]);
                    var typeName = entity.DynamicObjectType.Name;
                    var tableName = "";
                    string oaprocessid = "";

                    if (typeName.Equals("PAYBILL"))
                    {   // 付款单
                        tableName = "T_AP_PAYBILL";
                    }
                    else if (typeName.Equals("REFUNDBILL"))
                    {   // 收款退款单
                        tableName = "T_AR_REFUNDBILL";

                        // 收款退款单需要先有已付款确认的动作后才能同步OA
                        if (!IsBankStatusPaid(this.Context, payId))
                        {
                           throw new Exception($"收款退款单 {billNo} 尚未执行已付款确认，不允许同步OA！");
                        }
                    }

                    // 获取OA流程ID
                    oaprocessid = GetOAProcessId(this.Context, tableName, payId);

                    // 检查是否已同步成功
                    if (IsAlreadySynced(this.Context, tableName, payId))
                    {
                        throw new Exception("单据已成功同步OA，不需要再次同步");
                    }

                    // 检查流程编码是否为空
                    if (string.IsNullOrWhiteSpace(oaprocessid))
                    {
                       throw new Exception($"所选单据：{billNo} 不存在OA流程ID，不允许推送！");
                    }

                    bool isSync = oASync.skipCurrentCodeAsync(this.Context, oaprocessid);
                    // F_TWLG_OAStatus = 0（未反写）、1（已处理）
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

            // 记录同步结果日志
            string logMessage = syncFailCount > 0
                ? $"OA同步完成：成功 {syncSuccessCount} 笔，失败 {syncFailCount} 笔"
                : $"OA同步完成：成功 {syncSuccessCount} 笔";

            Kingdee.BOS.Log.Logger.Info("OASync", logMessage);
        }

        /// <summary>
        /// 检查单据是否已同步成功
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="tableName">表名</param>
        /// <param name="billId">单据ID</param>
        /// <returns>是否已同步成功</returns>
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

        /// <summary>
        /// 获取OA流程ID
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="tableName">表名</param>
        /// <param name="billId">单据ID</param>
        /// <returns>OA流程ID</returns>
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

        /// <summary>
        /// 检查收款退款单银行处理状态是否为已付款确认
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="billId">单据ID</param>
        /// <returns>银行处理状态是否为已付款确认</returns>
        private bool IsBankStatusPaid(Context ctx, long billId)
        {
            try
            {
                // 尝试使用正确的字段名，兼容大小写
                string sql = string.Format(@"
                    SELECT FBankStatus 
                    FROM T_AR_REFUNDBILLENTRY_B 
                    WHERE FID = {0}", billId);

                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    if (reader.Read())
                    {
                        string bankStatus = Convert.ToString(reader["FBankStatus"]);
                        // 已付款确认状态通常为 'F' 或其他表示已确认的状态
                        // 根据OASyncOperationSchedule.cs中的常量定义，已付款状态是 'F'
                        return bankStatus == "F"; // 'F' 通常表示已付款确认
                    }
                }
            }
            catch (Exception)
            {
                // 如果第一种方式失败，尝试另一种可能的字段名
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
                            return bankStatus == "F"; // 'F' 通常表示已付款确认
                        }
                    }
                }
                catch (Exception)
                {
                    // 如果都失败，返回false
                }
            }
            return false;
        }
    }
}
