using Kingdee.BOS.Core.List.PlugIn;
using Kingdee.BOS.Core.List.PlugIn.Args;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using System.ComponentModel;
using Kingdee.BOS.Util;

namespace LP.WXK.K3.App.ServicePlugIn
{
    /// <summary>
    /// 付款单列表插件：OA同步操作完成后刷新列表并提示
    /// </summary>
    [Description("【列表插件】付款单OA同步操作完成提示"), HotUpdate]
    public class PayBillListPlugIn : AbstractListPlugIn
    {
        public override void AfterDoOperation(AfterDoOperationEventArgs e)
        {
            base.AfterDoOperation(e);

            if (e.Operation.Operation.Equals("OASync", System.StringComparison.OrdinalIgnoreCase))
            {
                if (e.OperationResult.IsSuccess)
                {
                    this.View.ShowMessage("OA同步操作已完成");
                    this.View.Refresh();
                }
                else
                {
                    this.View.ShowErrMessage(e.OperationResult.GetString());
                }
            }
        }
    }

    /// <summary>
    /// 收款退款单列表插件：OA同步操作完成后刷新列表并提示
    /// </summary>
    [Description("【列表插件】收款退款单OA同步操作完成提示"), HotUpdate]
    public class RefundBillListPlugIn : AbstractListPlugIn
    {
        public override void AfterDoOperation(AfterDoOperationEventArgs e)
        {
            base.AfterDoOperation(e);

            if (e.Operation.Operation.Equals("OASync", System.StringComparison.OrdinalIgnoreCase))
            {
                if (e.OperationResult.IsSuccess)
                {
                    this.View.ShowMessage("OA同步操作已完成");
                    this.View.Refresh();
                }
                else
                {
                    this.View.ShowErrMessage(e.OperationResult.GetString());
                }
            }
        }
    }

    /// <summary>
    /// 银行交易明细列表插件：OA同步操作完成后刷新列表并提示
    /// </summary>
    [Description("【列表插件】银行交易明细OA同步操作完成提示"), HotUpdate]
    public class BankCashFlowListPlugIn : AbstractListPlugIn
    {
        public override void AfterDoOperation(AfterDoOperationEventArgs e)
        {
            base.AfterDoOperation(e);

            if (e.Operation.Operation.Equals("OASync", System.StringComparison.OrdinalIgnoreCase))
            {
                if (e.OperationResult.IsSuccess)
                {
                    this.View.ShowMessage("银行交易明细OA同步操作已完成");
                    this.View.Refresh();
                }
                else
                {
                    this.View.ShowErrMessage(e.OperationResult.GetString());
                }
            }
        }
    }
}
