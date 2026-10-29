-- Index ModifiedOn on every existing data table (data.t_X), as SchemaEngineService.CreateTableAsync does for new tables.
-- Pipeline searches answered by Azure AI Search re-read the records changed recently (the search index may not have caught up
-- with them yet); without this index that read scans the whole table (about 9 s on a 6M-row table).
--
-- The migration runner gives each batch 120 s and runs it in one transaction, so only SMALL tables are indexed here.
-- A table above @MaxRowsForMigration is skipped and reported: build those with
-- database/scripts/index_modifiedon_columns.sql, off-peak, outside the migration runner (it builds ONLINE).
-- Idempotent: an index that already exists is left alone.

-- Filtered indexes require QUOTED_IDENTIFIER ON (sqlcmd defaults to OFF).
SET QUOTED_IDENTIFIER ON;

DECLARE @MaxRowsForMigration BIGINT = 200000;
DECLARE @sql NVARCHAR(MAX), @table SYSNAME, @index SYSNAME, @rows BIGINT, @msg NVARCHAR(400);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        'data.t_' + CAST(t.Id AS NVARCHAR(20)),
        'IX_t_' + CAST(t.Id AS NVARCHAR(20)) + '_ModifiedOn',
        N'CREATE NONCLUSTERED INDEX IX_t_' + CAST(t.Id AS NVARCHAR(20)) + N'_ModifiedOn'
      + N' ON data.t_' + CAST(t.Id AS NVARCHAR(20)) + N'(ModifiedOn) WHERE IsDeleted = 0 AND ModifiedOn IS NOT NULL'
    FROM meta.AppTable t
    WHERE t.IsDeleted = 0
      AND OBJECT_ID('data.t_' + CAST(t.Id AS NVARCHAR(20))) IS NOT NULL;

OPEN c;
FETCH NEXT FROM c INTO @table, @index, @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name = @index)
    BEGIN
        -- sys.partitions (not sys.dm_db_partition_stats): reading it needs no VIEW DATABASE STATE permission.
        SELECT @rows = ISNULL(SUM(rows), 0)
        FROM sys.partitions
        WHERE object_id = OBJECT_ID(@table) AND index_id IN (0, 1);

        IF @rows > @MaxRowsForMigration
        BEGIN
            SET @msg = N'SKIPPED (' + CAST(@rows AS NVARCHAR(20)) + N' rows, build it with database/scripts/index_modifiedon_columns.sql): ' + @index;
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN TRY
            EXEC sys.sp_executesql @sql;
        END TRY
        BEGIN CATCH
            -- An index is only a speed-up: one that cannot be built must not stop the other migrations (when the error does not doom
            -- the transaction).
            IF XACT_STATE() = -1 THROW;
            SET @msg = N'FAILED (' + ERROR_MESSAGE() + N'): ' + @index;
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
    FETCH NEXT FROM c INTO @table, @index, @sql;
END
CLOSE c;
DEALLOCATE c;
