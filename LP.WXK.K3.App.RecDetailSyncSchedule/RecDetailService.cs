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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LP.WXK.K3.App.RecDetailSyncSchedule
{
    public class RecDetailService
    {
        // 回款明细保存地址
        private string url = "http://10.10.100.34:81/api/cube/restful/interface/saveOrUpdateModeData/paymentCollectionSave";

        // 系统标识
        private string systemid = "erpsysadmin";

        // 密码
        private string d_password = "12345678";

        // 操作人ID
        private string operatorId = "1001664";

        private readonly HttpClient httpClient;

        public RecDetailService()
        {
            httpClient = httpClient ?? new HttpClient();
        }

        public bool syncBill(Context context, long billId)
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
            var mainTable = getMainTableById(context, billId);

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


            Console.WriteLine("===请求参数datajson===" + JsonConvert.SerializeObject(paramDatajson));
            var paramsDict = new Dictionary<string, string>();
            paramsDict["datajson"] = JsonConvert.SerializeObject(paramDatajson);

            // 设置请求头
            request.Headers.Add("Content-Type", "application/x-www-form-urlencoded; charset=utf-8");

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
                    var responseContent = content.ReadAsStringAsync().GetAwaiter().GetResult();
                    JSONObject json = JSONObject.Parse(responseContent);

                    // todo这里处理返回信息
                    Console.WriteLine("成功" + json);

                }
            }
            return true;
        }

        private JObject getMainTableById(Context context, long billId)
        {
            var mainTable = new JObject();
            // 获取元数据服务
            IMetaDataService metadataService = ServiceHelper.GetService<IMetaDataService>();
            FormMetadata meta = metadataService.Load(context, "TWLG_OASyncLog") as FormMetadata;
            // 获取查看服务
            IViewService viewService = ServiceHelper.GetService<IViewService>();

            // 构建过滤条件
            QueryBuilderParemeter queryParameter = new QueryBuilderParemeter();
            queryParameter.BusinessInfo = meta.BusinessInfo;
            queryParameter.FilterClauseWihtKey = "Fid = " + billId;

            // 构建快捷过滤条件
            OQLFilter filter = new OQLFilter();
            filter.Add(new OQLFilterHeadEntityItem() { FilterString = "Fid = " + billId});
            /*DynamicObject[] objs = viewService.Load(context, meta.BusinessInfo.GetDynamicObjectType(), queryParameter);
            DynamicObject obj = objs[0];
            mainTable.Add("jffsezq", "0");
            mainTable.Add("dffsesr", "10000");
            mainTable.Add("rkzt", "0");
            mainTable.Add("sybm", "5126053");
            mainTable.Add("dfhm", "");
            mainTable.Add("dfkhjg", "");
            mainTable.Add("wrkje", "");
            mainTable.Add("remark", "");
            mainTable.Add("symc", "山西福源药业有限责任公司");
            mainTable.Add("zh", "ZH202512220001");
            mainTable.Add("jysj", "2025-12-22 19:18:00");
            mainTable.Add("frzt", "山西普德");
            mainTable.Add("zh01", "");
            mainTable.Add("bz", "");
            mainTable.Add("wwcwhdsj", "2025-12-22 19:18:00");
            mainTable.Add("id", "");
            mainTable.Add("jylsh", "2025122219190003");
            mainTable.Add("zhmc", "");
            mainTable.Add("jzrq", "");
            mainTable.Add("zhmc02", "");
            mainTable.Add("zhmc01", "");
            mainTable.Add("dfzh", "");
            mainTable.Add("zy", "");
            mainTable.Add("wwcwhdbs", "0");*/


            mainTable.Add("jffsezq", "0");
            mainTable.Add("dffsesr", "10000");
            mainTable.Add("rkzt", "0");
            mainTable.Add("sybm", "5126053");
            mainTable.Add("dfhm", "");
            mainTable.Add("dfkhjg", "");
            mainTable.Add("wrkje", "");
            mainTable.Add("remark", "");
            mainTable.Add("symc", "山西福源药业有限责任公司");
            mainTable.Add("zh", "ZH202512220001");
            mainTable.Add("jysj", "2025-12-22 19:18:00");
            mainTable.Add("frzt", "山西普德");
            mainTable.Add("zh01", "");
            mainTable.Add("bz", "");
            mainTable.Add("wwcwhdsj", "2025-12-22 19:18:00");
            mainTable.Add("id", "");
            mainTable.Add("jylsh", "2025122219190003");
            mainTable.Add("zhmc", "");
            mainTable.Add("jzrq", "");
            mainTable.Add("zhmc02", "");
            mainTable.Add("zhmc01", "");
            mainTable.Add("dfzh", "");
            mainTable.Add("zy", "");
            mainTable.Add("wwcwhdbs", "0");
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
