-- One-off, run per TENANT database, off-peak.
-- Adds the filtered index every Summary / Lookup / child-list read needs on each existing Reference column
-- (new Reference fields get it automatically from SchemaEngineService.AddColumnAsync).
-- Safe to re-run: skips indexes that already exist. Set @Execute = 0 first to only PRINT what it would build.
-- ONLINE = ON needs Enterprise / Azure SQL; remove that option on Standard edition (it then locks the table while building).

-- Filtered indexes require QUOTED_IDENTIFIER ON (sqlcmd defaults to OFF).
SET QUOTED_IDENTIFIER ON;

DECLARE @Execute BIT = 1;
DECLARE @sql NVARCHAR(MAX), @msg NVARCHAR(400);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        N'CREATE NONCLUSTERED INDEX IX_t_' + CAST(t.Id AS NVARCHAR(20)) + N'_f_' + CAST(f.Fid AS NVARCHAR(20))
      + N' ON data.t_' + CAST(t.Id AS NVARCHAR(20)) + N'(f_' + CAST(f.Fid AS NVARCHAR(20)) + N')'
      + N' WHERE IsDeleted = 0 WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, MAXDOP = 4);'
    FROM meta.AppField f
    JOIN core.FieldType ft ON ft.Id = f.FieldTypeId AND ft.Code = 'Reference'
    JOIN meta.AppTable t ON t.Id = f.AppTableId AND t.IsDeleted = 0
    WHERE f.IsDeleted = 0
      AND f.Fid IS NOT NULL
      AND COL_LENGTH('data.t_' + CAST(t.Id AS NVARCHAR(20)), 'f_' + CAST(f.Fid AS NVARCHAR(20))) IS NOT NULL
      AND NOT EXISTS (
            SELECT 1 FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID('data.t_' + CAST(t.Id AS NVARCHAR(20)))
              AND i.name = 'IX_t_' + CAST(t.Id AS NVARCHAR(20)) + '_f_' + CAST(f.Fid AS NVARCHAR(20)));

OPEN c;
FETCH NEXT FROM c INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @msg = CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 121) + N'  ' + @sql;
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    IF @Execute = 1 EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM c INTO @sql;
END
CLOSE c; DEALLOCATE c;
