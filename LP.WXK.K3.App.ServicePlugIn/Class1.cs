using System;
using Kingdee.BOS.WebApi.Client;
using Newtonsoft.Json.Linq;

namespace LP.WXK.K3.App.OASyncServicePlugIn
{
    /// <summary>
    /// 收款认领单 CN_RECCLAIMBIL：通过金蝶云 WebAPI 保存 → 提交 → 审核。
    /// 实现方式参考：<see href="https://vip.kingdee.com/article/150906163469612288"/>（K3CloudApiClient + Save / Submit / Audit 分步调用）。
    /// 账号、账套、站点地址由调用方传入，不在代码中写死密钥。
    /// </summary>
    public sealed class CNRecClaimBillWebApiClient
    {
        public const string FormId = "CN_RECCLAIMBIL";

        /// <summary>会话失效时接口返回的 MsgCode，需重新登录后重试一次。</summary>
        private const int MsgCodeSessionLost = 1;

        private readonly K3CloudApiClient _client;
        private readonly string _dbId;
        private readonly string _userName;
        private readonly string _password;
        private readonly int _lcId;

        public CNRecClaimBillWebApiClient(
            string serviceUrl,
            string dbId,
            string userName,
            string password,
            int lcId = 2052)
        {
            if (string.IsNullOrWhiteSpace(serviceUrl))
            {
                throw new ArgumentException("serviceUrl 不能为空。", nameof(serviceUrl));
            }

            _dbId = dbId ?? throw new ArgumentNullException(nameof(dbId));
            _userName = userName ?? throw new ArgumentNullException(nameof(userName));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _lcId = lcId;

            if (!serviceUrl.EndsWith("/", StringComparison.Ordinal))
            {
                serviceUrl += "/";
            }

            _client = new K3CloudApiClient(serviceUrl);
        }

        /// <summary>当前底层客户端，需自行扩展其它接口时可使用。</summary>
        public K3CloudApiClient Client => _client;

        /// <summary>登录账套。</summary>
        public bool Login()
        {
            string result = _client.ValidateLogin(_dbId, _userName, _password, _lcId);
            var jo = JObject.Parse(result);
            int loginType = jo["LoginResultType"]?.Value<int>() ?? 0;
            return loginType == 1;
        }

        /// <summary>
        /// 保存单据。saveJson 为 WebAPI Save 接口的完整请求体 JSON 字符串（须含 Model 等字段）。
        /// </summary>
        public WebApiBillResult Save(string saveJson)
        {
            return InvokeWithSessionRetry(
                () => _client.Save(FormId, saveJson),
                () => _client.Save(FormId, saveJson));
        }

        /// <summary>按内码提交。</summary>
        public WebApiBillResult Submit(long billId)
        {
            string data = BuildIdsPayload(billId);
            return InvokeWithSessionRetry(
                () => _client.Submit(FormId, data),
                () => _client.Submit(FormId, data));
        }

        /// <summary>按内码审核。</summary>
        public WebApiBillResult Audit(long billId)
        {
            string data = BuildIdsPayload(billId);
            return InvokeWithSessionRetry(
                () => _client.Audit(FormId, data),
                () => _client.Audit(FormId, data));
        }

        /// <summary>
        /// 保存成功后自动提交、审核（三步拆开，避免使用 Save 的 IsAutoSubmitAndAudit，与官方建议一致）。
        /// </summary>
        public WebApiBillResult SaveSubmitAndAudit(string saveJson)
        {
            WebApiBillResult saveResult = Save(saveJson);
            if (!saveResult.IsSuccess)
            {
                return saveResult;
            }

            WebApiBillResult submitResult = Submit(saveResult.BillId);
            if (!submitResult.IsSuccess)
            {
                return WebApiBillResult.Fail(
                    $"提交失败：{submitResult.Message}（保存已成功，内码 {saveResult.BillId}）",
                    saveResult.BillId,
                    saveResult.BillNo,
                    submitResult.RawResponse);
            }

            WebApiBillResult auditResult = Audit(saveResult.BillId);
            if (!auditResult.IsSuccess)
            {
                return WebApiBillResult.Fail(
                    $"审核失败：{auditResult.Message}（已保存并提交，内码 {saveResult.BillId}）",
                    saveResult.BillId,
                    saveResult.BillNo,
                    auditResult.RawResponse);
            }

            return WebApiBillResult.Ok(saveResult.BillId, saveResult.BillNo, auditResult.RawResponse);
        }

        private WebApiBillResult InvokeWithSessionRetry(Func<string> call, Func<string> retryCall)
        {
            string result = call();
            var jo = JObject.Parse(result);
            int msgCode = jo["Result"]?["ResponseStatus"]?["MsgCode"]?.Value<int>() ?? 0;
            if (msgCode == MsgCodeSessionLost)
            {
                if (!Login())
                {
                    return WebApiBillResult.Fail("会话失效且重新登录失败。", 0, null, result);
                }

                result = retryCall();
                jo = JObject.Parse(result);
            }

            return ParseBillResult(jo, result);
        }

        /// <summary>
        /// Submit / Audit 标准数据包（与多数环境一致；若你方环境要求 CreateOrgId 等必填，可在此扩展）。
        /// </summary>
        private static string BuildIdsPayload(long billId)
        {
            var o = new JObject
            {
                ["CreateOrgId"] = 0,
                ["Numbers"] = new JArray(),
                ["Ids"] = billId.ToString(),
                ["SelectedPostId"] = 0
            };
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static WebApiBillResult ParseBillResult(JObject jo, string raw)
        {
            var status = jo["Result"]?["ResponseStatus"];
            if (status == null)
            {
                return WebApiBillResult.Fail("响应缺少 Result.ResponseStatus。", 0, null, raw);
            }

            bool isSuccess = status["IsSuccess"]?.Value<bool>() ?? false;
            if (!isSuccess)
            {
                string err = FormatErrors(status["Errors"])
                             ?? status["Message"]?.Value<string>()
                             ?? status.ToString();
                return WebApiBillResult.Fail(err, 0, null, raw);
            }

            var entities = status["SuccessEntitys"] as JArray;
            if (entities == null || entities.Count == 0)
            {
                return WebApiBillResult.Fail("成功但无 SuccessEntitys，无法取得内码。", 0, null, raw);
            }

            long id = entities[0]["Id"]?.Value<long>() ?? 0;
            string number = entities[0]["Number"]?.Value<string>();
            return WebApiBillResult.Ok(id, number, raw);
        }

        private static string FormatErrors(JToken errors)
        {
            if (errors == null || errors.Type == JTokenType.Null)
            {
                return null;
            }

            if (errors is JArray arr && arr.Count > 0)
            {
                return string.Join("; ", arr);
            }

            return errors.ToString();
        }

    }

    /// <summary>WebAPI 单步或整链调用结果。</summary>
    public sealed class WebApiBillResult
    {
        public bool IsSuccess { get; private set; }
        public long BillId { get; private set; }
        public string BillNo { get; private set; }
        public string Message { get; private set; }
        public string RawResponse { get; private set; }

        public static WebApiBillResult Ok(long id, string number, string raw)
        {
            return new WebApiBillResult
            {
                IsSuccess = true,
                BillId = id,
                BillNo = number,
                Message = null,
                RawResponse = raw
            };
        }

        public static WebApiBillResult Fail(string message, long partialId, string partialNo, string raw)
        {
            return new WebApiBillResult
            {
                IsSuccess = false,
                BillId = partialId,
                BillNo = partialNo,
                Message = message,
                RawResponse = raw
            };
        }
    }
}
