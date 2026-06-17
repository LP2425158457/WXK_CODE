using Kingdee.BOS;
using Kingdee.BOS.App.Core;
using Kingdee.BOS.App.Core.Warn.Data;
using Kingdee.BOS.App.Data;
using Kingdee.BOS.Contracts;
using Kingdee.BOS.Core.DynamicForm;
using Kingdee.BOS.Core.DynamicForm.PlugIn;
using Kingdee.BOS.Core.DynamicForm.PlugIn.Args;
using Kingdee.BOS.Core.Metadata;
using Kingdee.BOS.Orm.DataEntity;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZK.WXK.App.ServicePlugIn
{
    [Description("采购订单提交处理oa的付款计划")]
    public class ZkPoorderSumbitServerPlugIn : AbstractOperationServicePlugIn
    {
        /// <summary>
        /// 加载需要获取的字段
        /// </summary>
        /// <param name="e"></param>
        public override void OnPreparePropertys(PreparePropertysEventArgs e)
        {
            base.OnPreparePropertys(e);
            e.FieldKeys.Add("FIinstallment");//付款计划标识
            #region 付款计划字段
            e.FieldKeys.Add("FYFRATIO");
            e.FieldKeys.Add("FYFAMOUNT");
            e.FieldKeys.Add("FISPREPAYMENT");
            e.FieldKeys.Add("FRemarks");
            e.FieldKeys.Add("FMATERIALSEQ");
            e.FieldKeys.Add("FOrderEntryId");
            e.FieldKeys.Add("FPayMaterialId");
            e.FieldKeys.Add("FPayPlanQty");
            e.FieldKeys.Add("FPayPlanPrice");
            e.FieldKeys.Add("FBasePayPlanQty");
            e.FieldKeys.Add("FPAYPLANPRICEUNITID");
            e.FieldKeys.Add("FBasePriceUnit");
            e.FieldKeys.Add("FPayMaterialDesc");
            e.FieldKeys.Add("FPURCHASEORDERNO");
            #endregion


        }

        /// <summary>
        /// 操作事物后事件（事务内触发）
        /// </summary>
        /// <param name="e"></param>
        /// <remarks>
        /// 1. 此事件在操作执行代码之后，操作的内部逻辑已经执行完毕
        /// 2. 此事件在操作事务提交之前
        /// 3. 此事件中的数据库处理，受操作的事务保护
        /// 4. 通常此事件，可以用来做同步数据，如同步生成其他单据，而且需要受事务保护
        /// </remarks>


        public override void EndOperationTransaction(EndOperationTransactionArgs e)
        {
            //循环选中的单据
            foreach (DynamicObject entity in e.DataEntitys)
            {  
             
                long ID = Convert.ToInt64(entity["ID"]);//单据主键id
                string number = Convert.ToString(entity["BillNo"]);//单据编号
                #region 判断oa付款计划是否有值，如果有值就进行处理，没有就不处理
                string sql = string.Format(@"/*dialect*/select a.FSeq,a.FYFRATIOOA,	a.FYFAMOUNTOA,a.FISPREPAYMENTOA,a.FREMARKSOA,a.FPAYMATERIALIDOA,a.FMATERIALSEQOA,a.FPAYPLANQTYOA,a.FPAYPLANPRICEOA,
b.FNAME as FPayMaterialDesc,k.FENTRYID,k.FPAYPLANPRICEUNITID,k.FBASEPRICEUNIT
From T_PUR_POORDERINSTALLMENTOA a 
left join T_BD_MATERIAL_L b on a.FPAYMATERIALIDOA=b.FMATERIALID and FLOCALEID=2052
left join (select t.FENTRYID,n.FPRICEUNITID as FPAYPLANPRICEUNITID,t.FBASEUNITID as FBASEPRICEUNIT,t.FID,t.FMATERIALID,t.FSEQ from T_PUR_POORDERENTRY t
left join  T_PUR_POORDERENTRY_F n on t.FENTRYID=n.FENTRYID) k  on k.FID=a.FID and k.FMATERIALID=a.FPAYMATERIALIDOA and k.FSEQ=a.FMATERIALSEQOA
where a.FID={0}", ID);
                DynamicObjectCollection Da = DBUtils.ExecuteDynamicObject(Context, sql);
                if (Da != null && Da.Count > 0)
                {   //重构付款计划数据包
                    DynamicObjectCollection doc = entity["FIinstallment"] as DynamicObjectCollection;
                    doc.Clear();//清除原来所以行数据
                   
                    for (int i = 0; i < Da.Count; i++)
                    {
                        DynamicObject docs = new DynamicObject(doc.DynamicCollectionItemPropertyType);//新增空白行
                                                                                                      //oa过来更改的单据 第一次明细行主键EID

                        string sqlid = string.Format(@"/*dialect*/
DECLARE @count INT;
-- 这里设置要获取的种子值的数量
SET @count = 1;
DECLARE @icount INT;
SET NOCOUNT ON;
SET @icount = 0;
DECLARE @OutIdTable TABLE ( Id BIGINT );
DECLARE @lastValue INT;
DECLARE @trancount INT;
SET @trancount = @@TRANCOUNT;
IF @trancount > 0
    SAVE TRANSACTION sp;
ELSE
    BEGIN TRANSACTION;
BEGIN TRY
    IF @count <= 100
        BEGIN
            WHILE @icount < @count
                BEGIN
                    INSERT  INTO z_PUR_POORDERINSTALLMENT
                            ( Column1 )
                    VALUES  ( 0  -- Column1 - int
                              );
                    INSERT  INTO @OutIdTable
                            ( Id )
                    VALUES  ( SCOPE_IDENTITY() );
                    SET @icount = @icount + 1;
                END;
            SELECT  Id
            FROM    @OutIdTable;
        END;
    ELSE
        BEGIN
            UPDATE  z_PUR_POORDERINSTALLMENT WITH ( TABLOCK )
            SET     Column1 = Column1;
            EXEC sp_executesql N'insert into z_PUR_POORDERINSTALLMENT(column1)
select top(@count) 1 from master..spt_values x cross join master..spt_values y  cross join (select top 200 * from master..spt_values) n',
                N'@count int', @count = @count;
            SELECT  Id
            FROM    z_PUR_POORDERINSTALLMENT WITH ( TABLOCK );
        END;
    IF @trancount > 0
        ROLLBACK TRANSACTION sp;
    ELSE
        ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
    IF @trancount > 0
        ROLLBACK TRANSACTION sp;
    ELSE
        ROLLBACK TRANSACTION;
    DECLARE @errmessage NVARCHAR(4000);
    DECLARE @errserverity INT;
    DECLARE @errstate INT;
    SELECT  @errmessage = ERROR_MESSAGE() ,
            @errserverity = ERROR_SEVERITY() ,
            @errstate = ERROR_STATE();
    RAISERROR(@errmessage,@errserverity,@errstate);
END CATCH;
");
                        DynamicObjectCollection DaID = DBUtils.ExecuteDynamicObject(Context, sqlid);

                        docs["Id"] = Convert.ToInt64(DaID[0]["Id"]);
                        docs["seq"] = Convert.ToInt32(Da[i]["FSeq"]);//序号
                        docs["YFRATIO"] = Convert.ToDecimal(Da[i]["FYFRATIOOA"]);//应付比例(%)
                        docs["YFAMOUNT"] = Convert.ToDecimal(Da[i]["FYFAMOUNTOA"]);//应付金额
                        docs["ISPREPAYMENT"] = Convert.ToInt32(Da[i]["FISPREPAYMENTOA"]);//是否预付
                        docs["FRemarks"] = Convert.ToString(Da[i]["FREMARKSOA"]);//备注


                        docs["FMATERIALSEQ"] = Convert.ToInt32(Da[i]["FMATERIALSEQOA"]);//物料行号
                        docs["OrderEntryId"] = Convert.ToInt64(Da[i]["FENTRYID"]);//订单明细行内码
                        docs["PayMaterialId_Id"] = Convert.ToInt64(Da[i]["FPAYMATERIALIDOA"]);//物料编码
                        docs["PayPlanQty"] = Convert.ToDecimal(Da[i]["FPAYPLANQTYOA"]);//数量
                        docs["PayPlanPrice"] = Convert.ToDecimal(Da[i]["FPAYPLANPRICEOA"]);//含税单价
                        docs["BasePayPlanQty"] = Convert.ToDecimal(Da[i]["FPAYPLANQTYOA"]);//数量基本单位
                        docs["PAYPLANPRICEUNITID_Id"] = Convert.ToInt64(Da[i]["FPAYPLANPRICEUNITID"]);//计价单位
                        docs["BasePriceUnit_Id"] = Convert.ToInt64(Da[i]["FBASEPRICEUNIT"]);//计价基本单位
                        docs["PayMaterialDesc"] = Convert.ToString(Da[i]["FPayMaterialDesc"]);//物料说明
                        docs["FPURCHASEORDERNO"] = number;//采购订单号
                        doc.Add(docs);
                      
                    }


                }
                // 保存变更后的数据

                new BusinessDataWriter(Context).Save(e.DataEntitys);

          


                #endregion


            }
        }

    }
}
