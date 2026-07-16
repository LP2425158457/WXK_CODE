using System;
using System.Net.Http;
using Kingdee.BOS.JSON;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.App;
using Kingdee.BOS.Core.Metadata;
using Kingdee.BOS;

namespace LP.WXK.K3.App.ServicePlugIn
{
    public class OASyncService
    {
        private const string DEFAULT_BASE_URL = "https://172.17.14.93:80";
        private const string DEFAULT_SKIP_NODE_PATH = "/api/xfd/skipCurrentNode";
        private const string PAYBILL_BASE_URL = "http://10.10.100.34:81";
        private const string PAYBILL_SKIP_NODE_PATH = "/api/xfd/sctgfskipCurrentNode";

        private readonly string baseURL;
        private readonly string skipNodePath;
        private readonly string appId = "f975a20b-8632-4b0a-9be7-342b010be988";
        private string secrit = "";
        private string spk = "";
        private readonly HttpClient httpClient;
        private readonly Program p;

        public OASyncService() : this(DEFAULT_BASE_URL, DEFAULT_SKIP_NODE_PATH)
        {
        }

        public OASyncService(bool isPayBill) : this(
            isPayBill ? PAYBILL_BASE_URL : DEFAULT_BASE_URL,
            isPayBill ? PAYBILL_SKIP_NODE_PATH : DEFAULT_SKIP_NODE_PATH)
        {
        }

        private OASyncService(string baseURL, string skipNodePath)
        {
            this.baseURL = baseURL;
            this.skipNodePath = skipNodePath;
            httpClient = new HttpClient();
            p = new Program();
        }

        public bool skipCurrentCodeAsync(Context context, string requestId)
        {
            string url = $"{baseURL}{skipNodePath}?requestId={requestId}";
            string secret = regist();
            string token = applyToken(secret);

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("appid", appId);
            httpRequest.Headers.Add("token", token);
            string userid = p.EncryptByPublicKey("1", spk);
            httpRequest.Headers.Add("userid", userid);

            var response = httpClient.SendAsync(httpRequest).GetAwaiter().GetResult();
            var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                string errorMsg = $"HTTP请求失败，状态码: {response.StatusCode}，响应内容: {responseContent}";
                saveOALog(context, requestId, url, errorMsg, false);
                throw new Exception(errorMsg);
            }

            JSONObject json = null;
            try
            {
                json = JSONObject.Parse(responseContent);
            }
            catch (Exception ex)
            {
                string errorMsg = $"解析响应JSON失败，响应内容: {responseContent}，异常: {ex.Message}";
                saveOALog(context, requestId, url, errorMsg, false);
                throw new Exception(errorMsg);
            }

            saveOALog(context, requestId, url, Convert.ToString(json), true);

            if (json.ContainsKey("code"))
            {
                string code = Convert.ToString(json["code"]);
                if ("200".Equals(code))
                {
                    return true;
                }
                else
                {
                    string errorMsg = $"OA接口返回错误，业务响应码: {code}，响应内容: {responseContent}";
                    throw new Exception(errorMsg);
                }
            }

            string unknownError = $"OA接口返回格式异常，未包含code字段，响应内容: {responseContent}";
            throw new Exception(unknownError);
        }

        public string regist()
        {
            if (!string.IsNullOrEmpty(secrit) && !string.IsNullOrEmpty(spk))
            {
                return p.EncryptByPublicKey(secrit, spk);
            }

            string url = baseURL + "/api/ec/dev/auth/regist";
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("appid", appId);

            var response = httpClient.SendAsync(httpRequest).GetAwaiter().GetResult();
            var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OA注册接口HTTP请求失败，状态码: {response.StatusCode}，响应内容: {responseContent}");
            }

            JSONObject json = null;
            try
            {
                json = JSONObject.Parse(responseContent);
            }
            catch (Exception ex)
            {
                throw new Exception($"OA注册接口解析响应JSON失败，响应内容: {responseContent}，异常: {ex.Message}");
            }

            if (!json.ContainsKey("status") || !Convert.ToBoolean(json["status"]))
            {
                throw new Exception($"OA注册失败，响应内容: {responseContent}");
            }

            if (json.ContainsKey("secrit"))
                secrit = Convert.ToString(json["secrit"]);
            if (json.ContainsKey("spk"))
                spk = Convert.ToString(json["spk"]);

            if (string.IsNullOrEmpty(secrit) || string.IsNullOrEmpty(spk))
            {
                throw new Exception($"OA注册返回的密钥为空，响应内容: {responseContent}");
            }

            return p.EncryptByPublicKey(secrit, spk);
        }

        public string applyToken(string secret)
        {
            string url = baseURL + "/api/ec/dev/auth/applytoken";
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("appid", appId);
            httpRequest.Headers.Add("secret", secret);

            var response = httpClient.SendAsync(httpRequest).GetAwaiter().GetResult();
            var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OA获取Token接口HTTP请求失败，状态码: {response.StatusCode}，响应内容: {responseContent}");
            }

            JSONObject json = null;
            try
            {
                json = JSONObject.Parse(responseContent);
            }
            catch (Exception ex)
            {
                throw new Exception($"OA获取Token接口解析响应JSON失败，响应内容: {responseContent}，异常: {ex.Message}");
            }

            if (!json.ContainsKey("status") || !Convert.ToBoolean(json["status"]))
            {
                throw new Exception($"OA获取Token失败，响应内容: {responseContent}");
            }

            if (!json.ContainsKey("token"))
            {
                throw new Exception($"OA获取Token返回为空，响应内容: {responseContent}");
            }

            return Convert.ToString(json["token"]);
        }

        public void saveOALog(Context context, string number, string req, string resp, bool isSuccess)
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
    }
}
