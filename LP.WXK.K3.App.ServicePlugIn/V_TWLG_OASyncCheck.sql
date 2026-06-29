-- ============================================================
-- 银行交易明细 → OA 推送 自检视图（修正版 v2）
-- 依据 OASyncRecDetailSchedule(自动) / OASyncRecDetailOperationServicePlugIn(手动)
-- 把每个准入条件、最终判定、推送结果都展成布尔列
-- 数据库：K/3 Cloud (SQL Server)
-- ============================================================
-- 修正记录：
--   v1 -> v2:
--     1) T_ORG_Organizations_L 字段不一定是 FNAME，环境不同字段名也不同
--        -> 去掉对 _L 表的 JOIN，直接用 FNUMBER 当显示名
--     2) 自定义字段物理列名漏了下划线：F_TWLG_SyncDate（不是 FTWLG_SyncDate）
-- ============================================================

IF OBJECT_ID('dbo.V_TWLG_OASyncCheck', 'V') IS NOT NULL
    DROP VIEW dbo.V_TWLG_OASyncCheck;
GO

CREATE VIEW dbo.V_TWLG_OASyncCheck
AS
SELECT
    h.FID                                                     AS 单据ID,
    h.FBILLNO                                                 AS 单据编号,
    h.FTRANSDATE                                              AS 交易日期,
    h.FEXPLANATION                                            AS 摘要,
    h.FOppBankAcntName                                        AS 对方户名,
    h.FSETTLENO                                               AS 交易流水号,
    h.FCREDITAMOUNT                                           AS 贷方金额,
    h.FDEBITAMOUNT                                            AS 借方金额,
    o.FNUMBER                                                 AS 结算组织编码,
    -- 兜底用 FNUMBER 作显示名；如果 T_ORG_Organizations_L 的 FNAME 在你环境里存在，
    -- 可以自行加 LEFT JOIN T_ORG_Organizations_L ON FORGID=o.FORGID AND FLOCALEID=2052
    o.FNUMBER                                                 AS 结算组织名称,

    -- 单据自身状态
    CASE h.FDOCUMENTSTATUS WHEN 'A' THEN N'暂存'
                           WHEN 'B' THEN N'审核中'
                           WHEN 'C' THEN N'已审核'
                           WHEN 'D' THEN N'重新审核'
                           ELSE h.FDOCUMENTSTATUS END         AS 单据状态,

    -- ============================================================
    -- 节点判定（0=不满足，1=满足）
    -- ============================================================

    -- 节点1：结算组织编码 ∈ {102,104,108}，且组织已审核未禁用
    CASE WHEN o.FNUMBER IN ('102', '104', '108')
              AND o.FDOCUMENTSTATUS = 'C'
              AND o.FFORBIDSTATUS  = 'A'
         THEN 1 ELSE 0 END                                     AS [节点1_结算组织允许],

    -- 节点2：贷方金额 > 0
    CASE WHEN ISNULL(h.FCREDITAMOUNT, 0) > 0
         THEN 1 ELSE 0 END                                     AS [节点2_贷方金额大于0],

    -- 节点3：单据已审核
    CASE WHEN h.FDOCUMENTSTATUS = 'C'
         THEN 1 ELSE 0 END                                     AS [节点3_单据已审核],

    -- 节点4：交易流水号未关联任何已审核的收款认领单
    CASE WHEN NOT EXISTS (
              SELECT 1
              FROM dbo.T_CN_RECCLAIMBILLENTRY e
              INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
              WHERE e.FBNKSEQNO       = h.FSETTLENO
                AND c.FDOCUMENTSTATUS = 'C')
         THEN 1 ELSE 0 END                                     AS [节点4_未关联认领单],

    -- 节点5（自动推送专用）：对方户名 ≠ "山西普德药业有限公司"
    CASE WHEN ISNULL(h.FOppBankAcntName, '') <> N'山西普德药业有限公司'
         THEN 1 ELSE 0 END                                     AS [节点5_对方户名非排除],

    -- 节点6（自动推送专用）：摘要命中 T_TWLG_RecDetailFilter 任一白名单关键字
    CASE WHEN EXISTS (
              SELECT 1
              FROM dbo.T_TWLG_RecDetailFilter f
              WHERE f.FDOCUMENTSTATUS = 'C'
                AND f.FFORBIDSTATUS  = 'A'
                AND ISNULL(f.F_TWLG_FilterContent, '') <> ''
                AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%')
         THEN 1 ELSE 0 END                                     AS [节点6_摘要命中白名单],

    -- ============================================================
    -- 同步状态 & 推送结果
    -- ============================================================
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

    -- 最近一次同步时间（来自 OA 同步日志表 T_TWLG_OASyncLog，按单据号取最大同步日期）
    -- 注意：自定义字段物理列名是 F_TWLG_<字段名>，中间有下划线
    -- 如果你的字段名不一样，请执行 SELECT TOP 1 * FROM T_TWLG_OASyncLog 看实际列名后调整
    lg.LastSyncDate                                            AS 最近同步时间,

    -- ============================================================
    -- 综合准入结论
    -- ============================================================

    -- 自动推送准入：6 个节点全部命中 + 同步状态=未同步
    CASE WHEN o.FNUMBER IN ('102', '104', '108')
              AND o.FDOCUMENTSTATUS = 'C'
              AND o.FFORBIDSTATUS  = 'A'
              AND ISNULL(h.FCREDITAMOUNT, 0) > 0
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
                    AND f.FFORBIDSTATUS  = 'A'
                    AND ISNULL(f.F_TWLG_FilterContent, '') <> ''
                    AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%')
              AND ISNULL(h.F_TWLG_OASyncStatus, 0) = 0
         THEN 1 ELSE 0 END                                     AS [自动推送_准入通过],

    -- 手动推送准入：节点1/2/4 命中 + 同步状态≠1（已成功）
    CASE WHEN o.FNUMBER IN ('102', '104', '108')
              AND o.FDOCUMENTSTATUS = 'C'
              AND o.FFORBIDSTATUS  = 'A'
              AND ISNULL(h.FCREDITAMOUNT, 0) > 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.T_CN_RECCLAIMBILLENTRY e
                  INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
                  WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
              AND ISNULL(h.F_TWLG_OASyncStatus, 0) <> 1
         THEN 1 ELSE 0 END                                     AS [手动推送_准入通过],

    -- ============================================================
    -- 阻断原因（按优先级给出第一条）
    -- ============================================================
    CASE
        WHEN o.FNUMBER IS NULL                                      THEN N'结算组织为空'
        WHEN o.FNUMBER NOT IN ('102', '104', '108')                 THEN N'结算组织不在允许范围(' + ISNULL(o.FNUMBER, N'空') + N')'
        WHEN o.FDOCUMENTSTATUS <> 'C' OR o.FFORBIDSTATUS <> 'A'     THEN N'结算组织未审核或已禁用'
        WHEN ISNULL(h.FCREDITAMOUNT, 0) <= 0                        THEN N'贷方金额为0'
        WHEN EXISTS (
              SELECT 1
              FROM dbo.T_CN_RECCLAIMBILLENTRY e
              INNER JOIN dbo.T_CN_RECCLAIMBILL c ON e.FID = c.FID
              WHERE e.FBNKSEQNO = h.FSETTLENO AND c.FDOCUMENTSTATUS = 'C')
                                                                  THEN N'已关联收款认领单'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 1                  THEN N'已成功推送过'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 2                  THEN N'上次同步失败'
        WHEN ISNULL(h.F_TWLG_OASyncStatus, 0) = 3                  THEN N'已排除(对方户名=山西普德)'
        ELSE N''
    END                                                          AS 阻断原因
FROM dbo.T_CN_BANKCASHFLOW h
LEFT JOIN dbo.T_ORG_Organizations o
       ON h.FSETTLEORGID = o.FORGID
OUTER APPLY (
    SELECT TOP 1 lg0.F_TWLG_SyncDate AS LastSyncDate
    FROM dbo.T_TWLG_OASyncLog lg0
    WHERE lg0.FBILLNO = h.FBILLNO
    ORDER BY lg0.F_TWLG_SyncDate DESC
) lg;
GO

-- ============================================================
-- 常用排查脚本（取消注释即可执行）
-- ============================================================

-- 0) 第一次部署时，建议先跑这一行确认表和列都对得上
/*
SELECT TOP 1 * FROM dbo.T_TWLG_OASyncLog;
-- 重点看有没有 F_TWLG_SyncDate / FBILLNO 这两列
*/

-- 1) 看所有"自动推送应该推、但没推"的可疑单据
/*
SELECT *
FROM dbo.V_TWLG_OASyncCheck
WHERE [节点1_结算组织允许]   = 1
  AND [节点2_贷方金额大于0]  = 1
  AND [节点3_单据已审核]     = 1
  AND [节点4_未关联认领单]   = 1
  AND [节点5_对方户名非排除] = 1
  AND [节点6_摘要命中白名单] = 0     -- 这里就是缺摘要白名单
  AND 是否已成功推送 = 0;
*/

-- 2) 看所有"准入通过、但还没推出去"的卡点
/*
SELECT *
FROM dbo.V_TWLG_OASyncCheck
WHERE [自动推送_准入通过] = 1
  AND 是否已成功推送 = 0;
*/

-- 3) 看所有推送失败的流水
/*
SELECT *
FROM dbo.V_TWLG_OASyncCheck
WHERE 同步状态 = N'同步失败';
*/

-- 4) 按阻断原因聚合统计
/*
SELECT 阻断原因, COUNT(*) AS 笔数
FROM dbo.V_TWLG_OASyncCheck
WHERE 阻断原因 <> N''
GROUP BY 阻断原因
ORDER BY 笔数 DESC;
*/

-- 5) 看每个白名单关键字实际命中多少流水（用来判断白名单是否够全）
/*
SELECT f.F_TWLG_FilterContent AS 白名单关键字, COUNT(*) AS 命中笔数
FROM dbo.T_TWLG_RecDetailFilter f
INNER JOIN dbo.T_CN_BANKCASHFLOW h
   ON h.FDOCUMENTSTATUS = 'C'
  AND h.FCREDITAMOUNT  > 0
  AND h.F_TWLG_OASyncStatus = 0
  AND ISNULL(h.FOppBankAcntName,'') <> N'山西普德药业有限公司'
  AND h.FEXPLANATION LIKE '%' + f.F_TWLG_FilterContent + '%'
WHERE f.FDOCUMENTSTATUS = 'C' AND f.FFORBIDSTATUS = 'A'
GROUP BY f.F_TWLG_FilterContent
ORDER BY 命中笔数 DESC;
*/