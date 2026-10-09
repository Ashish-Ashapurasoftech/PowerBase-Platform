-- Index every existing Reference column (data.t_X.f_N) so Summary / Lookup / child-list reads
-- (WHERE IsDeleted = 0 AND f_N IN (...)) seek instead of scanning the whole child table.
-- New Reference fields get the same index from SchemaEngineService.AddColumnAsync.
--
-- The migration runner gives each batch 120 s and runs it in one transaction, so only SMALL tables are indexed here.
-- A table above @MaxRowsForMigration is skipped and reported: build those with
-- database/scripts/index_reference_columns.sql, off-peak, outside the migration runner.
-- Idempotent: an index that already exists is left alone.

-- Filtered indexes require QUOTED_IDENTIFIER ON (sqlcmd defaults to OFF).
SET QUOTED_IDENTIFIER ON;

DECLARE @MaxRowsForMigration BIGINT = 200000;
DECLARE @sql NVARCHAR(MAX), @table SYSNAME, @index SYSNAME, @rows BIGINT, @msg NVARCHAR(400);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        'data.t_' + CAST(t.Id AS NVARCHAR(20)),
        'IX_t_' + CAST(t.Id AS NVARCHAR(20)) + '_f_' + CAST(f.Fid AS NVARCHAR(20)),
        N'CREATE NONCLUSTERED INDEX IX_t_' + CAST(t.Id AS NVARCHAR(20)) + N'_f_' + CAST(f.Fid AS NVARCHAR(20))
      + N' ON data.t_' + CAST(t.Id AS NVARCHAR(20)) + N'(f_' + CAST(f.Fid AS NVARCHAR(20)) + N') WHERE IsDeleted = 0'
    FROM meta.AppField f
    JOIN core.FieldType ft ON ft.Id = f.FieldTypeId AND ft.Code = 'Reference'
    JOIN meta.AppTable t ON t.Id = f.AppTableId AND t.IsDeleted = 0
    WHERE f.IsDeleted = 0
      AND f.Fid IS NOT NULL
      AND COL_LENGTH('data.t_' + CAST(t.Id AS NVARCHAR(20)), 'f_' + CAST(f.Fid AS NVARCHAR(20))) IS NOT NULL;

OPEN c;
FETCH NEXT FROM c INTO @table, @index, @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name = @index)
    BEGIN
        SELECT @rows = ISNULL(SUM(row_count), 0)
        FROM sys.dm_db_partition_stats
        WHERE object_id = OBJECT_ID(@table) AND index_id IN (0, 1);

        IF @rows > @MaxRowsForMigration
        BEGIN
            SET @msg = N'SKIPPED (' + CAST(@rows AS NVARCHAR(20)) + N' rows, build it with database/scripts/index_reference_columns.sql): ' + @index;
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
            EXEC sys.sp_executesql @sql;
    END
    FETCH NEXT FROM c INTO @table, @index, @sql;
END
CLOSE c;
DEALLOCATE c;
