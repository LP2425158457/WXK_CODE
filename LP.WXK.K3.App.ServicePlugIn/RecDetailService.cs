using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Kingdee.BOS;
using Kingdee.BOS.App;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Core.Metadata;
using Kingdee.BOS.Core.SqlBuilder;
using Kingdee.BOS.JSON;
using Kingdee.BOS.Orm.DataEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LP.WXK.K3.App.RecDetailSyncSchedule
{
    public class RecDetailService
    {
        // 回款明细保存地址
        // private string url = "http://10.10.100.34:81/api/cube/restful/interface/saveOrUpdateModeData/paymentCollectionSave";
        private string url = "http://172.17.14.93:80/api/cube/restful/interface/saveOrUpdateModeData/paymentCollectionSave";

        // 系统标识
        private string systemid = "erpsysadmin";

        // 密码
        private string d_password = "12345678";

        // 操作人ID
        private string operatorId = "1001664";

        /// <summary>
        /// 结算组织编码（FNUMBER）与zhmc01的映射
        /// 108 西藏中卫诚康药业有限公司=1；102 洋浦京泰药业有限公司=2；104 内蒙古白医制药股份有限公司=3
        /// </summary>
        private static readonly Dictionary<string, string> ORG_NUMBER_TO_ZHMC01 = new Dictionary<string, string>
        {
            { "108", "1" },
            { "102", "2" },
            { "104", "3" }
        };

        private readonly HttpClient httpClient;

        public RecDetailService()
        {
            httpClient = httpClient ?? new HttpClient();
        }

        public bool syncBill(Context context, string billNo)
        {
            bool isSuccess = false;
            string requestContent = "";
            string responseContent = "";

            try
            {
                // restful接口url
                var request = new HttpRequestMessage(HttpMethod.Post, url);

                // 当前日期
                string currentDate = GetCurrentDate();
                // 当前时间
                string currentTime = GetCurrentTime();
                // 获取时间戳
                string currentTimeTamp = GetTimestamp();

                var paramDatajson = new Dictionary<string, object>();

                // 构建header
                var header = new Dictionary<string, string>();
                header["systemid"] = systemid;
                header["currentDateTime"] = currentTimeTamp;
                string md5Source = systemid + d_password + currentTimeTamp;
                string md5OfStr = GetMD5Str(md5Source).ToLower();
                // Md5是：系统标识+密码+时间戳 并且md5加密的结果
                header["Md5"] = md5OfStr;

                // 构建operationinfo参数
                var operationinfo = new JObject();
                operationinfo["operationDate"] = currentDate;
                operationinfo["operator"] = operatorId;
                operationinfo["operationTime"] = currentTime;

                // 构建mainTable
                var mainTable = getMainTableById(context, billNo);

                // 构建 detail1 中的 operate
                var operate = new JObject();
                operate.Add("action", "SaveOrUpdate");

                // 构建 detail1 的 data（空对象）
                var data = new JObject();

                // 构建 detail1 数组
                var detail1List = new JArray();
                detail1List.Add(new JObject()
                {
                    { "operate", operate },
                    { "data", data }
                });

                // 构建 data 数组中的第一个元素
                var dataItem = new JObject();
                dataItem.Add("operationinfo", operationinfo);
                dataItem.Add("mainTable", mainTable);
                dataItem.Add("detail1", detail1List);

                // 构建 data 数组
                var dataArray = new JArray();
                dataArray.Add(dataItem);

                // 将 data 和 header 添加到根对象
                paramDatajson.Add("data", dataArray);
                paramDatajson.Add("header", header);

                requestContent = JsonConvert.SerializeObject(paramDatajson);
                var paramsDict = new Dictionary<string, string>();
                paramsDict["datajson"] = requestContent;

                // 创建表单内容
                var formParams = new List<KeyValuePair<string, string>>();
                foreach (var param in paramsDict)
                {
                    formParams.Add(new KeyValuePair<string, string>(param.Key, param.Value));
                }

                request.Content = new FormUrlEncodedContent(formParams);

                // 发送请求
                using (HttpResponseMessage response = httpClient.SendAsync(request).GetAwaiter().GetResult())
                {
                    using (HttpContent content = response.Content)
                    {
                        responseContent = content.ReadAsStringAsync().GetAwaiter().GetResult();
                        JSONObject json = JSONObject.Parse(responseContent);

                        // 处理返回信息
                        if (json.ContainsKey("code"))
                        {
                            string code = Convert.ToString(json["code"]);
                            isSuccess = "success".Equals(code, StringComparison.OrdinalIgnoreCase) ||
                                        "200".Equals(code);
                        }
                        else if (json.ContainsKey("status"))
                        {
                            string status = Convert.ToString(json["status"]);
                            isSuccess = "1".Equals(status) || "success".Equals(status, StringComparison.OrdinalIgnoreCase);
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                responseContent = $"Exception: {ex.Message}";
            }
            finally
            {
                saveOALog(context, billNo, requestContent, responseContent, isSuccess);
            }

            // 同步失败则抛出异常
            if (!isSuccess)
            {
                throw new Exception($"同步收款单失败：{responseContent}");
            }

            return isSuccess;
        }

        /// <summary>
        /// 保存OA日志
        /// </summary>
        /// <param name="context">上下文</param>
        /// <param name="number">单据编号</param>
        /// <param name="req">请求内容</param>
        /// <param name="resp">响应内容</param>
        /// <param name="isSuccess">是否成功</param>
        private void saveOALog(Context context, string number, string req, string resp, bool isSuccess)
        {
            try
            {
                IMetaDataService metadataService = ServiceHelper.GetService<IMetaDataService>();
                FormMetadata meta = metadataService.Load(context, "TWLG_OASyncLog") as FormMetadata;

                if (meta == null)
                {
                    return;
                }

                ISaveService saveService = ServiceHelper.GetService<ISaveService>();

                DynamicObject oaSyncLog = meta.BusinessInfo.GetDynamicObjectType().CreateInstance() as DynamicObject;
                if (oaSyncLog != null)
                {
                    oaSyncLog["BillNo"] = number;
                    oaSyncLog["TWLG_Request"] = req;
                    oaSyncLog["TWLG_Response"] = resp;
                    oaSyncLog["TWLG_Success"] = isSuccess;
                    oaSyncLog["TWLG_CreateDate"] = DateTime.Now;
                    oaSyncLog["TWLG_SyncDate"] = DateTime.Now;

                    DynamicObject[] objects = new DynamicObject[] { oaSyncLog };
                    saveService.Save(context, objects);
                }
            }
            catch (Exception)
            {
            }
        }

        private JObject getMainTableById(Context context, string billNo)
        {
            var mainTable = new JObject();
            // 获取元数据服务
            IMetaDataService metadataService = ServiceHelper.GetService<IMetaDataService>();
            FormMetadata meta = metadataService.Load(context, "WB_RecBankTradeDetail") as FormMetadata;
            // 获取查看服务
            IViewService viewService = ServiceHelper.GetService<IViewService>();

            // 构建快捷过滤条件
            OQLFilter filter = new OQLFilter();
            filter.Add(new OQLFilterHeadEntityItem() { FilterString = $"FBILLNO = '{billNo}'" });

            // 构建关心的字段片段信息
            List<SelectorItemInfo> selectors = new List<SelectorItemInfo>();
            // id
            selectors.Add(new SelectorItemInfo("FBillNo"));
            selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//  法人主体	frzt	varchar(1000)	mainTable	是		传入：浏览框的数据标题（名称）
            selectors.Add(new SelectorItemInfo("FBANKACNTNO"));//账号	zh	varchar(200)	mainTable	是		
            selectors.Add(new SelectorItemInfo("FBANKACNTNAME"));//账户名称	zhmc	varchar(200)	mainTable			
            selectors.Add(new SelectorItemInfo("FTRANSDATE"));//交易时间	jysj	varchar(100)	mainTable	是		传入格式：yyyy-MM-dd HH:mm:ss
            selectors.Add(new SelectorItemInfo("FDEBITAMOUNT"));//支取（借方发生额）	jffsezq	decimal(38,2)	mainTable	是		
            selectors.Add(new SelectorItemInfo("FCREDITAMOUNT"));//收入（贷方发生额）	dffsesr	decimal(38,2)	mainTable	是		
            selectors.Add(new SelectorItemInfo("FCurrency"));//币种	bz	varchar(50)	mainTable			
            selectors.Add(new SelectorItemInfo("FOppBankAcntName"));//对方户名	dfhm	varchar(200)	mainTable			
            selectors.Add(new SelectorItemInfo("FOppBankAcntNo"));//对方账号	dfzh	varchar(200)	mainTable			
            selectors.Add(new SelectorItemInfo("FOppOpenBankName"));//对方开户机构	dfkhjg	varchar(200)	mainTable			
            selectors.Add(new SelectorItemInfo("FTOACCOUNTDATE"));//记账日期	jzrq	char(10)	mainTable			传入格式：yyyy-MM-dd
            selectors.Add(new SelectorItemInfo("FEXPLANATION"));//摘要	zy	varchar(500)	mainTable			
            selectors.Add(new SelectorItemInfo("FEXPLANATION"));//备注	remark	varchar(500)	mainTable			
            selectors.Add(new SelectorItemInfo("FSETTLENO"));//交易流水号	jylsh	varchar(200)	mainTable	是		
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//未认款金额	wrkje	decimal(38,2)	mainTable			默认传0
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//认款状态	rkzt	int	mainTable	是		枚举：[0:未认款,1:认款中,2:已认款]默认传0:未认款
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//财务核对标识	wwcwhdbs	int	mainTable	是		枚举：[0:是,1:否]默认传0:是
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//财务核对时间	wwcwhdsj	varchar(100)	mainTable			传入格式：yyyy-MM-dd HH:mm:ss
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//商业名称	symc	varchar(1000)	mainTable	是		传入：浏览框的数据标题(名称)
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//账户01	zh01	varchar(100)	mainTable			
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//账户名称00	zhmc01	int	mainTable			枚举：[0:山西普德药业有限公司,1:西藏中卫诚康药业有限公司]
            // selectors.Add(new SelectorItemInfo("FSETTLEORGID"));//账户名称01	zhmc02	int	mainTable			枚举：[0:山西普德药业有限公司,1:西藏中卫诚康药业有限公司,2:西藏普德医药有限公司1,3:西藏普德医药有限公司2]
            selectors.Add(new SelectorItemInfo("FPAYUNIT"));//商业编码	sybm	varchar(100)	mainTable	是		传商业公司id

            DynamicObject[] objs = viewService.Load(context, meta.BusinessInfo, selectors, filter);
            DynamicObject obj = objs[0];


            //  mainTable.Add("id", "");// int类型，新增时不用传
            DynamicObject settleOrg = (DynamicObject)obj["FSETTLEORGID"];
            mainTable.Add("frzt", Convert.ToString(settleOrg["Name"]));// 法人主体
            mainTable.Add("zh", Convert.ToString(obj["FBANKACNTNO"]));// 账号
            mainTable.Add("zhmc", Convert.ToString(obj["FBANKACNTNAME"]));// 账号名称

            // 根据结算组织编码（FNUMBER）动态计算zhmc01
            // 108 西藏中卫诚康药业有限公司=1；102 洋浦京泰药业有限公司=2；104 内蒙古白医制药股份有限公司=3
            string zhmc01Value = "";
            object settleOrgNumberObj = settleOrg["Number"];
            if (settleOrgNumberObj != null)
            {
                string settleOrgNumber = Convert.ToString(settleOrgNumberObj);
                if (ORG_NUMBER_TO_ZHMC01.ContainsKey(settleOrgNumber))
                {
                    zhmc01Value = ORG_NUMBER_TO_ZHMC01[settleOrgNumber];
                }
            }
            mainTable.Add("jysj", Convert.ToDateTime(obj["FTRANSDATE"]).ToString("yyyy-MM-dd HH:mm:ss"));// 交易时间
            mainTable.Add("jffsezq", Convert.ToString(obj["FDEBITAMOUNT"]));//支取（借方发生额）
            mainTable.Add("dffsesr", Convert.ToString(obj["FCREDITAMOUNT"]));//收入（贷方发生额）
            mainTable.Add("bz", Convert.ToString(obj["FCurrency"]));//币种
            mainTable.Add("dfhm", Convert.ToString(obj["FOppBankAcntName"]));//对方户名
            mainTable.Add("dfzh", Convert.ToString(obj["FOppBankAcntNo"]));//对方账号
            mainTable.Add("dfkhjg", Convert.ToString(obj["FOppOpenBankName"]));//对方开户机构
            mainTable.Add("jzrq", Convert.ToDateTime(obj["FTOACCOUNTDATE"]).ToString("yyyy-MM-dd"));//记账日期
            mainTable.Add("zy", Convert.ToString(obj["FEXPLANATION"]));//摘要
            mainTable.Add("remark", Convert.ToString(obj["FEXPLANATION"]));//备注
            mainTable.Add("jylsh", Convert.ToString(obj["FSETTLENO"]));//交易流水号
            mainTable.Add("wrkje", "0");
            mainTable.Add("rkzt", "0");
            mainTable.Add("wwcwhdbs", "0");
            mainTable.Add("wwcwhdsj", "2025-12-22 19:18:00");
            DynamicObject payUnit = (DynamicObject)obj["FPAYUNIT"];
            if (payUnit != null)
            {
                mainTable.Add("sybm", Convert.ToString(payUnit["Number"]));
                mainTable.Add("symc", Convert.ToString(payUnit["Name"]));
            }
            else
            {
                mainTable.Add("sybm", "");
                mainTable.Add("symc", "");
            }
            mainTable.Add("zh01", "");
            mainTable.Add("zhmc02", "1");
            mainTable.Add("zhmc01", zhmc01Value);
            mainTable.Add("khdasfcz", "0");// 客户档案是否存在，存在传0，不存在传1
            // 5)	推送oa字段赋值时，校验“对方账户名称”是否在客户档案，若是，则oa回款明细的“客户档案是否存在”=是，若不存在，则“客户档案是否存在”=否

            return mainTable;
        }

        public static string GetMD5Str(string plainText)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }

                string md5code = sb.ToString();
                // 如果生成数字未满32位，需要前面补0
                while (md5code.Length < 32)
                {
                    md5code = "0" + md5code;
                }
                return md5code;
            }
        }

        public static string GetCurrentTime()
        {
            DateTime now = DateTime.Now;
            return now.ToString("HH:mm:ss");
        }

        public static string GetCurrentDate()
        {
            DateTime now = DateTime.Now;
            return now.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 获取当前日期时间。 YYYY-MM-DD HH:MM:SS
        /// </summary>
        /// <returns>当前日期时间</returns>
        public static string GetCurDateTime()
        {
            DateTime now = DateTime.Now;
            return now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// 获取时间戳   格式如：19990101235959
        /// </summary>
        /// <returns></returns>
        public static string GetTimestamp()
        {
            return GetCurDateTime().Replace("-", "").Replace(":", "").Replace(" ", "");
        }

        public static int GetIntValue(string v, int def)
        {
            try
            {
                return int.Parse(v);
            }
            catch (Exception ex)
            {
                return def;
            }
        }

        public static string Null2String(object s)
        {
            return s == null ? "" : s.ToString();
        }
    }
}
