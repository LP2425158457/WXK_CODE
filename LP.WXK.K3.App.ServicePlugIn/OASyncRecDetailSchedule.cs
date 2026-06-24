using Kingdee.BOS;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Core;
using Kingdee.BOS.App.Data;
using LP.WXK.K3.App.RecDetailSyncSchedule;
using System;
using System.Collections.Generic;
using System.Data;

namespace LP.WXK.K3.App.ServicePlugIn
{
    /// <summary>
    /// 银行交易明细同步OA定时任务
    /// 定时查询符合条件的银行交易明细，推送到OA回款明细
    /// </summary>
    public class OASyncRecDetailSchedule : IScheduleService
    {
        /// <summary>
        /// 不推送OA的对方账户名称
        /// </summary>
        private const string EXCLUDED_OPP_ACCOUNT_NAME = "山西普德药业有限公司";

        /// <summary>
        /// 允许推送OA回款明细的组织编码（FNUMBER）列表
        /// 102 洋浦京泰药业有限公司、104 内蒙古白医制药股份有限公司、108 西藏中卫诚康药业有限公司
        /// </summary>
        private static readonly HashSet<string> ALLOWED_SETTLE_ORG_NUMBERS = new HashSet<string> { "102", "104", "108" };

        /// <summary>
        /// 组织编码（FNUMBER）与zhmc01的映射
        /// 108 西藏中卫诚康药业有限公司=1；102 洋浦京泰药业有限公司=2；104 内蒙古白医制药股份有限公司=3
        /// </summary>
        private static readonly Dictionary<string, string> ORG_NUMBER_TO_ZHMC01 = new Dictionary<string, string>
        {
            { "108", "1" },
            { "102", "2" },
            { "104", "3" }
        };

        /// <summary>
        /// 定时任务执行入口
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="schedule">定时任务配置，Parameter为推送起始时间（格式：yyyy-MM-dd），仅推送该时间之后的数据</param>
        public void Run(Context ctx, Schedule schedule)
        {
            RecDetailService recDetailSync = new RecDetailService();

            List<string> summaryFilters = GetSummaryFilters(ctx);

            // 从定时任务参数中获取推送起始时间
            string pushStartTime = schedule.Parameters;

            ProcessRecDetailBills(ctx, recDetailSync, summaryFilters, pushStartTime);
        }

        /// <summary>
        /// 获取摘要过滤条件列表
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <returns>摘要过滤条件列表</returns>
        private List<string> GetSummaryFilters(Context ctx)
        {
            List<string> filters = new List<string>();

            try
            {
                string sql = @"
                    SELECT F_TWLG_FilterContent 
                    FROM T_TWLG_RecDetailFilter 
                    WHERE FDOCUMENTSTATUS = 'C' 
                      AND FFORBIDSTATUS = 'A'";

                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    while (reader.Read())
                    {
                        string filterContent = Convert.ToString(reader["F_TWLG_FilterContent"]);
                        if (!string.IsNullOrWhiteSpace(filterContent))
                        {
                            filters.Add(filterContent.Trim());
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return filters;
        }

        /// <summary>
        /// 处理银行交易明细
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="recDetailSync">回款明细同步服务</param>
        /// <param name="summaryFilters">摘要过滤条件列表</param>
        /// <param name="pushStartTime">推送起始时间（格式：yyyy-MM-dd），仅推送该时间之后的数据，为空则不过滤</param>
        private void ProcessRecDetailBills(Context ctx, RecDetailService recDetailSync, List<string> summaryFilters, string pushStartTime)
        {
            if (summaryFilters == null || summaryFilters.Count == 0)
            {
                return;
            }

            try
            {
                string sql = @"
                    SELECT h.FID, h.FBILLNO, h.FEXPLANATION, h.FOppBankAcntName, h.FSETTLEORGID
                    FROM T_CN_BANKCASHFLOW h
                    INNER JOIN T_ORG_Organizations o ON h.FSETTLEORGID = o.FORGID
                    WHERE h.F_TWLG_OASyncStatus = 0
                      AND h.FDOCUMENTSTATUS = 'C'
                      AND h.FCREDITAMOUNT > 0
                      AND o.FNUMBER IN ('102', '104', '108')
                      AND o.FDOCUMENTSTATUS = 'C'
                      AND o.FFORBIDSTATUS = 'A'
                      AND NOT EXISTS (
                          SELECT 1 FROM T_CN_RECCLAIMBILLENTRY e
                          INNER JOIN T_CN_RECCLAIMBILL c ON e.FID = c.FID
                          WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C'
                      )";

                // 如果指定了推送起始时间，则只推送该时间之后的数据
                if (!string.IsNullOrWhiteSpace(pushStartTime))
                {
                    DateTime filterDate;
                    if (DateTime.TryParse(pushStartTime, out filterDate))
                    {
                        string filterDateStr = filterDate.ToString("yyyy-MM-dd");
                        sql += string.Format(" AND h.FTRANSDATE >= '{0}'", filterDateStr);
                    }
                }

                using (IDataReader reader = DBUtils.ExecuteReader(ctx, sql))
                {
                    while (reader.Read())
                    {
                        long billId = Convert.ToInt64(reader["FID"]);
                        string billNo = Convert.ToString(reader["FBILLNO"]);
                        try
                        {
                            string summary = Convert.ToString(reader["FEXPLANATION"]) ?? "";
                            string oppAccountName = Convert.ToString(reader["FOppBankAcntName"]) ?? "";

                            if (oppAccountName.Equals(EXCLUDED_OPP_ACCOUNT_NAME, StringComparison.OrdinalIgnoreCase))
                            {
                                UpdateSyncStatus(ctx, billId, 3);
                                continue;
                            }

                            bool isMatch = false;
                            foreach (string filter in summaryFilters)
                            {
                                if (summary.Contains(filter))
                                {
                                    isMatch = true;
                                    break;
                                }
                            }

                            if (!isMatch)
                            {
                                continue;
                            }

                            bool isSync = recDetailSync.syncBill(ctx, billNo);

                            int status = isSync ? 1 : 2;
                            UpdateSyncStatus(ctx, billId, status);
                        }
                        catch (Exception)
                        {
                            UpdateSyncStatus(ctx, billId, 2);
                            continue;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 更新同步状态（同时写F_TWLG_OASyncStatus和F_TWLG_OAStatus，以F_TWLG_OASyncStatus为准）
        /// </summary>
        /// <param name="ctx">上下文</param>
        /// <param name="billId">单据ID</param>
        /// <param name="status">状态：0-未同步，1-已同步，2-同步失败，3-已排除</param>
        private void UpdateSyncStatus(Context ctx, long billId, int status)
        {
            try
            {
                // F_TWLG_OAStatus 兼容旧值：3-已排除不在旧枚举里，归为2-失败以保持兼容
                int oaStatus = status == 3 ? 2 : status;
                string sql = string.Format(
                    "UPDATE T_CN_BANKCASHFLOW SET F_TWLG_OASyncStatus = {0}, F_TWLG_OAStatus = {1} WHERE FID = {2}",
                    status, oaStatus, billId);
                DBUtils.Execute(ctx, sql);
            }
            catch (Exception)
            {
            }
        }
    }
}
