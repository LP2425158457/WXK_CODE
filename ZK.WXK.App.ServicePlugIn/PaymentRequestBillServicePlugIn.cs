// Decompiled with JetBrains decompiler
// Type: ZK.WXK.App.ServicePlugIn.PaymentRequestBillServicePlugIn
// Assembly: ZK.WXK.App.ServicePlugIn, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: A54968EE-15A2-44B6-B992-DE19A4B8BDCB
// Assembly location: C:\Users\ADD\Desktop\ZK.WXK.App.ServicePlugIn.dll

using Kingdee.BOS;
using Kingdee.BOS.App.Data;
using Kingdee.BOS.Core.Bill.PlugIn;
using Kingdee.BOS.Core.DynamicForm;
using Kingdee.BOS.Core.DynamicForm.PlugIn;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.JSON;
using Kingdee.BOS.Orm.DataEntity;
using Kingdee.BOS.Orm.Metadata.DataEntity;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Text;

namespace ZK.WXK.App.ServicePlugIn
{
    public class PaymentRequestBillServicePlugIn : AbstractBillPlugIn
    {
        public override void ButtonClick(ButtonClickEventArgs e)
        {
            if (e.Key == "F_ZKYD_OALINK")
            {
                string UserInfoQuerysSql = string.Format("select FUSERID,FUSERACCOUNT from T_SEC_USER where FUSERID = '{0}'", this.View.Context.UserId);
                string UserAccount = "";
                DynamicObjectCollection UserInfoObjs = DBUtils.ExecuteDynamicObject(Context, UserInfoQuerysSql);
                if (UserInfoObjs.Count != 0 && UserInfoObjs != null)
                {
                    UserAccount = Convert.ToString(UserInfoObjs[0]["FUSERACCOUNT"]);
                }
                else
                {
                    this.View.ShowErrMessage("当前用户没有指定用户账号！");
                }
                /*;"*/
                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create("http://oa.wxkpharma.cn/ssologin/getToken?");
                httpWebRequest.Method = "POST";
                httpWebRequest.ContentType = "application/x-www-form-urlencoded";
                byte[] bytes = Encoding.UTF8.GetBytes("appid=ssss&loginid=" + UserAccount);
                httpWebRequest.ContentLength = bytes.Length;
                using (Stream requestStream = httpWebRequest.GetRequestStream())
                    requestStream.Write(bytes, 0, bytes.Length);
                using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                {
                    using (Stream responseStream = httpWebResponse.GetResponseStream())
                    {
                        using (StreamReader streamReader = new StreamReader(responseStream, Encoding.UTF8))
                        {
                            string ReqInfo = streamReader.ReadToEnd();
                            if (ReqInfo != "" && ReqInfo != null)
                            {
                                if (ReqInfo.Contains("Token获取失败"))
                                {
                                    this.View.ShowErrMessage(ReqInfo);
                                }
                                else
                                {
                                    long requestId = 0;
                                    string OANumber = Convert.ToString(this.View.Model.GetValue("F_ZKYD_OANumber"));
                                    if (OANumber != null && OANumber != "")
                                    {
                                        try
                                        {
                                            //Server = 192.168.1.208; Database = ecology; User Id = zkerp; Password = wxk@888888;
                                            //Server = 10.10.100.34; Database = ecology; User Id = sa; Password = wxk@2022;
                                            using (SqlConnection connection = new SqlConnection("Server = 192.168.1.208; Database = ecology; User Id = zkerp; Password = wxk@888888;"))
                                            {
                                                connection.Open();
                                                using (SqlCommand sqlCommand = new SqlCommand("SELECT requestid, lcbh FROM v_ht where lcbh = '" + OANumber + "'", connection))
                                                {
                                                    using (SqlDataReader sqlDataReader = sqlCommand.ExecuteReader())
                                                    {
                                                        while (sqlDataReader.Read())
                                                        {
                                                            requestId = Convert.ToInt32(sqlDataReader["requestid"].ToString());
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (SqlException ex)
                                        {
                                            Console.WriteLine("数据库错误: " + ex.Message);
                                        }
                                        string url = "http://oa.wxkpharma.cn/spa/workflow/index_form.jsp?ssoToken=" + ReqInfo + "#/main/workflow/req?requestid=" + requestId.ToString();
                                        //string url = "http://10.10.100.34/spa/workflow/index_form.jsp?ssoToken=" + ReqInfo + "#/main/workflow/req?requestid=" + requestId.ToString();
                                        JSONObject jsonObject = new JSONObject();
                                        jsonObject["source"] = url;
                                        jsonObject["height"] = 545;
                                        jsonObject["width"] = 810;
                                        jsonObject["isweb"] = true;
                                        jsonObject["title"] = "OA流程";
                                        this.View.AddAction("ShowKDWebbrowseForm", jsonObject);
                                        this.View.SendDynamicFormAction(this.View);
                                    }
                                    else
                                    {
                                        this.View.ShowErrMessage("请先填入OA流程编码！");
                                    }
                                }
                            }
                            else
                            {
                                this.View.ShowErrMessage(ReqInfo);
                            }
                        }
                    }
                }
            }
        }
    }
}
