using Kingdee.BOS.Core.Bill.PlugIn;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LP.WXK.TEST
{
    /// <summary>
    /// 【单据插件】Hello World
    /// </summary>
    [Description("【单据插件】Hello World"), HotUpdate]
    public class HelloWorldBillPlugin : AbstractBillPlugIn
    {
        public override void AfterBindData(EventArgs e)
        {
            base.AfterBindData(e);
            this.View.ShowMessage("Hello World");
        }
    }
}
