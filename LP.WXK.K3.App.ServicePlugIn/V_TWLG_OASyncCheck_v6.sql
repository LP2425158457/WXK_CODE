-- ============================================================
-- 银行交易明细 -> OA 推送 自检视图 v6（对齐代码版）
-- 相对 v5 的修正：
--   1. 组织过滤下沉到 INNER JOIN：非 102/104/108、组织未审核/已禁用的单据不进结果集
--   2. 节点6 回补白名单 EXISTS（T_TWLG_RecDetailFilter / F_TWLG_FilterContent，探针B已确认）
--   3. 手动准入不含节点3/5/6（代码不显式检查，且按钮绑在自定义操作上）
--   4. 阻断原因拆为 自动阻断原因 / 手动阻断原因 两列
--   5. 新增 [白名单配置正常] 提示列（白名单为空时自动推送整体 return）
-- 组织条件已在 FROM 过滤，节点1 恒为1，仅作占位保留
-- ============================================================

IF OBJECT_ID('dbo.V_TWLG_OASyncCheck', 'V') IS NOT NULL
    DROP VIEW dbo.V_TWLG_OASyncCheck;
GO

CREATE VIEW dbo.V_TWLG_OASyncCheck
AS
SELECT TOP 100 PERCENT
    h.FID                                                     AS 单据ID,
    h.FBILLNO                                                 AS 单据编号,
    h.FTRANSDATE                                              AS 交易日期,
    h.FEXPLANATION                                            AS 摘要,
    h.FOppBankAcntName                                        AS 对方户名,
    h.FSETTLENO                                               AS 交易流水号,
    h.FCREDITAMOUNT                                           AS 贷方金额,
    h.FDEBITAMOUNT                                            AS 借方金额,
    o.FNUMBER                                                 AS 结算组织编码,
    o.FNUMBER                                                 AS 结算组织名称,

    CASE h.FDOCUMENTSTATUS WHEN 'A' THEN N'暂存'
                           WHEN 'B' THEN N'审核中'
                           WHEN 'C' THEN N'已审核'
                           WHEN 'D' THEN N'重新审核'
                           ELSE h.FDOCUMENTSTATUS END         AS 单据状态,

    -- 节点1：组织 ∈ 白名单 且 已审核未禁用（已于 INNER JOIN 过滤，恒为1）
    1                                                          AS [节点1_结算组织允许],

    -- 节点2：贷方金额 > 0（自动/手动都查）
    CASE WHEN ISNULL(h.FCREDITAMOUNT, 0) > 0
         THEN 1 ELSE 0 END                                     AS [节点2_贷方金额大于0],

    -- 节点3：单据已审核（自动检查；手动代码不显式检查，仅靠操作绑定时机保证）
    CASE WHEN h.FDOCUMENTSTATUS = 'C'
         THEN 1 ELSE 0 END                                     AS [节点3_单据已审核],

    -- 节点4：未关联已审核的收款认领单（自动/手动都查）
    CASE WHEN NOT EXISTS (
              SELECT 1
              FROM dbo.T_CN_RECCLAIMBILLENTRY e
              INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
              WHERE e.FBNKSEQNO       = h.FSETTLENO
                AND c.FDOCUMENTSTATUS = 'C')
         THEN 1 ELSE 0 END                                     AS [节点4_未关联认领单],

    -- 节点5：对方户名 ≠ 山西普德（仅自动检查）
    CASE WHEN ISNULL(h.FOppBankAcntName, '') <> N'山西普德药业有限公司'
         THEN 1 ELSE 0 END                                     AS [节点5_对方户名非排除],

    -- 节点6：摘要命中白名单 T_TWLG_RecDetailFilter（仅自动检查）
    -- 对应代码 summary.Contains(filter)
    CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.T_TWLG_RecDetailFilter f
              WHERE f.FDOCUMENTSTATUS = 'C'
                AND f.FFORBIDSTATUS   = 'A'
                AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%')
         THEN 1 ELSE 0 END                                     AS [节点6_摘要命中白名单],

    -- 白名单配置是否正常（自动推送前置条件：白名单表不能为空，否则整段 return）
    CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.T_TWLG_RecDetailFilter f
              WHERE f.FDOCUMENTSTATUS = 'C'
                AND f.FFORBIDSTATUS   = 'A')
         THEN 1 ELSE 0 END                                     AS [白名单配置正常],

    -- 同步状态
    h.F_TWLG_OASyncStatus                                     AS 同步状态原始值,
    CASE ISNULL(h.F_TWLG_OASyncStatus, 0)
        WHEN 0 THEN N'未同步'
        WHEN 1 THEN N'已同步'
        WHEN 2 THEN N'同步失败'
        WHEN 3 THEN N'已排除'
        ELSE CONVERT(NVARCHAR(10), h.F_TWLG_OASyncStatus)
    END                                                       AS 同步状态,

    CASE WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 1
         THEN 1 ELSE 0 END                                     AS [是否已成功推送],

    -- v6 仍不查 OA 日志表，先填 NULL
    CAST(NULL AS DATETIME)                                    AS 最近同步时间,

    -- 自动准入：节点2+3+4+5+6 + 白名单正常 + status=0（组织已于JOIN过滤）
    CASE WHEN ISNULL(h.FCREDITAMOUNT, 0) > 0
              AND h.FDOCUMENTSTATUS = 'C'
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.T_CN_RECCLAIMBILLENTRY e
                  INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
                  WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
              AND ISNULL(h.FOppBankAcntName, '') <> N'山西普德药业有限公司'
              AND EXISTS (
                  SELECT 1
                  FROM dbo.T_TWLG_RecDetailFilter f
                  WHERE f.FDOCUMENTSTATUS = 'C'
                    AND f.FFORBIDSTATUS   = 'A'
                    AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%')
              AND ISNULL(h.F_TWLG_OASyncStatus, 0) = 0
         THEN 1 ELSE 0 END                                     AS [自动推送_准入通过],

    -- 手动准入：节点2+4 + status≠1（不含节点3/5/6，忠实代码；组织已于JOIN过滤）
    CASE WHEN ISNULL(h.FCREDITAMOUNT, 0) > 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.T_CN_RECCLAIMBILLENTRY e
                  INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
                  WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
              AND ISNULL(h.F_TWLG_OASyncStatus, 0) <> 1
         THEN 1 ELSE 0 END                                     AS [手动推送_准入通过],

    -- 自动阻断原因（组织相关分支已移除，因 JOIN 已过滤）
    CASE
        WHEN ISNULL(h.FCREDITAMOUNT, 0) <= 0                        THEN N'贷方金额为0'
        WHEN h.FDOCUMENTSTATUS <> 'C'                               THEN N'单据未审核'
        WHEN EXISTS (
              SELECT 1 FROM dbo.T_CN_RECCLAIMBILLENTRY e
              INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
              WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
                                                                  THEN N'已关联收款认领单'
        WHEN ISNULL(h.FOppBankAcntName, '') = N'山西普德药业有限公司' THEN N'对方户名为山西普德(→status=3)'
        WHEN NOT EXISTS (
              SELECT 1 FROM dbo.T_TWLG_RecDetailFilter f
              WHERE f.FDOCUMENTSTATUS='C' AND f.FFORBIDSTATUS='A'
                AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%')
                                                                  THEN N'摘要未命中白名单'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 1                  THEN N'已成功推送过'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 2                  THEN N'上次同步失败'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 3                  THEN N'已排除'
        ELSE N''
    END                                                          AS 自动阻断原因,

    -- 手动阻断原因（组织相关分支已移除，因 JOIN 已过滤）
    CASE
        WHEN ISNULL(h.FCREDITAMOUNT, 0) <= 0                        THEN N'贷方金额为0'
        WHEN EXISTS (
              SELECT 1 FROM dbo.T_CN_RECCLAIMBILLENTRY e
              INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
              WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
                                                                  THEN N'已关联收款认领单'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 1                  THEN N'已成功推送过'
        ELSE N''
    END                                                          AS 手动阻断原因

FROM dbo.T_CN_BANKCASHFLOW h
INNER JOIN dbo.T_ORG_Organizations o
       ON h.FSETTLEORGID  = o.FORGID
      AND o.FNUMBER      IN ('102', '104', '108')
      AND o.FDOCUMENTSTATUS = 'C'
      AND o.FFORBIDSTATUS   = 'A'
ORDER BY h.FTRANSDATE DESC;
GO
