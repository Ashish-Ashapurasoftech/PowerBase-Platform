-- Run with sqlcmd -b against SQL Server. Only session-local temporary tables are used.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

CREATE TABLE #Source (
    Id bigint IDENTITY(1001, 2) NOT NULL PRIMARY KEY,
    PublicId uniqueidentifier NOT NULL DEFAULT NEWID(),
    CreatedOn datetime2 NULL, CreatedBy bigint NULL,
    ModifiedOn datetime2 NULL, ModifiedBy bigint NULL,
    f_6 nvarchar(100) NULL, f_7 decimal(18, 2) NULL, f_8 bit NULL,
    IsDeleted bit NOT NULL DEFAULT 0
);
;WITH numbers AS (
    SELECT TOP (252) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects
)
INSERT #Source(f_6, f_7, f_8, IsDeleted)
SELECT N'ગુજરાતી-' + CONVERT(nvarchar(10), n), n - 1, n % 2,
    CASE WHEN n = 252 THEN 1 ELSE 0 END FROM numbers;

-- Same snapshot projection and locking strategy as PipelineRecordSearchService.
SELECT TOP (0) ISNULL(Id + CONVERT(bigint, 0), CONVERT(bigint, 0)) AS Id,
    PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, f_6, f_7, f_8
INTO #CopyRecordsSnapshot FROM #Source;
CREATE UNIQUE CLUSTERED INDEX IX_CopyRecordsSnapshot ON #CopyRecordsSnapshot(Id);

IF COLUMNPROPERTY(OBJECT_ID('tempdb..#CopyRecordsSnapshot'), 'Id', 'IsIdentity') <> 0
    THROW 51000, 'Snapshot must not inherit IDENTITY.', 1;

BEGIN TRANSACTION;
INSERT INTO #CopyRecordsSnapshot
SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, f_6, f_7, f_8
FROM #Source WITH (HOLDLOCK) WHERE IsDeleted = 0;
COMMIT;

IF (SELECT COUNT(*) FROM #CopyRecordsSnapshot) <> 251
    THROW 51001, 'Snapshot must include all active rows only.', 1;
IF EXISTS (
    SELECT Id, PublicId, f_6, f_7, f_8 FROM #Source WHERE IsDeleted = 0
    EXCEPT SELECT Id, PublicId, f_6, f_7, f_8 FROM #CopyRecordsSnapshot
)
    THROW 51002, 'Snapshot changed source identities or field values.', 1;

-- Source changes after materialization cannot alter the snapshot (including same-table copy).
INSERT #Source(f_6) VALUES (N'Added later');
UPDATE #Source SET f_6 = N'Changed later';
IF (SELECT COUNT(*) FROM #CopyRecordsSnapshot) <> 251
    THROW 51003, 'Snapshot must stay stable after source changes.', 1;
IF EXISTS (SELECT 1 FROM #CopyRecordsSnapshot WHERE f_6 = N'Changed later')
    THROW 51004, 'Snapshot values must stay stable.', 1;

DECLARE @afterId bigint = 0, @read int = 0, @pages int = 0, @pageCount int;
WHILE 1 = 1
BEGIN
    SELECT @pageCount = COUNT(*), @afterId = COALESCE(MAX(Id), @afterId)
    FROM (SELECT TOP (250) Id FROM #CopyRecordsSnapshot WHERE Id > @afterId ORDER BY Id) AS page;
    IF @pageCount = 0 BREAK;
    SET @read += @pageCount;
    SET @pages += 1;
END;
IF @read <> 251 OR @pages <> 2
    THROW 51005, 'Keyset paging lost or repeated records.', 1;

-- READPAST must work on this same session after both commit and rollback.
DECLARE @claimed bigint;
SELECT TOP (1) @claimed = Id FROM #Source WITH (READPAST);
IF (SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID) <> 2
    THROW 51006, 'Snapshot leaked an incompatible isolation level.', 1;
BEGIN TRANSACTION;
SELECT TOP (1) @claimed = Id FROM #Source WITH (HOLDLOCK);
ROLLBACK;
SELECT TOP (1) @claimed = Id FROM #Source WITH (READPAST);

PRINT 'PASS: non-identity snapshot, preserved IDs/values, deleted-row filter, stable snapshot, 251-row paging, READPAST after commit and rollback.';
