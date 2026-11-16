-- Copy App with data, "including file attachments": the copy gets its OWN copy of every file and revision (plan D5,
-- changed 2026-11-06). Quickbase keeps a copy independent of its source the same way. Migration 091 shared the
-- original's blobs instead (meta.SharedFileBlob with a RefCount); this replaces that:
--
--   meta.usp_CopyAppData            copies File values as they are (or NULL when files are left out), as before, but no
--                                   longer registers shared paths. @RowsEndPercent: where the records end on the progress
--                                   bar; 80 when the C# Files stage follows (AppCopyRepository.CopyFilesAsync), which
--                                   copies the files in storage and writes the new paths.
--   meta.usp_DiscardStaleAppCopies  also returns the PublicIds of the copies it removed, so C# deletes their files
--                                   (named "appcopy-{PublicId}-…").
--   meta.usp_RegisterSharedFileBlobs, meta.SharedFileBlob   dropped.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE meta.usp_CopyAppData
    @RunId          BIGINT,
    @ChunkSize      INT = 100000,
    @RowsEndPercent INT = 97
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @sourceAppId BIGINT, @newAppId BIGINT, @includeFiles BIT;
    SELECT @sourceAppId = r.SourceAppId, @newAppId = r.NewAppId, @includeFiles = r.IncludeFiles
    FROM meta.AppCopyRun r
    JOIN meta.App a ON a.Id = r.NewAppId
    WHERE r.Id = @RunId AND r.Status = 'Running' AND a.Status = 'Draft' AND a.IsDeleted = 0;
    IF @newAppId IS NULL
        THROW 50404, N'The copy was not found or is not running.', 1;

    IF @RowsEndPercent IS NULL OR @RowsEndPercent < 5 OR @RowsEndPercent > 97 SET @RowsEndPercent = 97;

    -- Every chunk commits on its own, so a big copy never holds locks or log for long. Called inside a transaction
    -- (tests), everything stays in the caller's transaction instead.
    DECLARE @ownTran BIT = IIF(@@TRANCOUNT = 0, 1, 0);

    -- Original table -> copied table. Every live table has an alias that is unique in its app
    -- (UX_AppTable_AppId_Alias), and the schema copy keeps it.
    CREATE TABLE #pair (
        OldTableId BIGINT        NOT NULL PRIMARY KEY,
        NewTableId BIGINT        NOT NULL,
        Name       NVARCHAR(200) NOT NULL,
        RowTotal   BIGINT        NOT NULL DEFAULT 0
    );
    INSERT INTO #pair (OldTableId, NewTableId, Name)
    SELECT o.Id, n.Id, n.Name
    FROM meta.AppTable o
    JOIN meta.AppTable n ON n.AppId = @newAppId AND n.IsDeleted = 0
                        AND (n.Alias = o.Alias OR (n.Alias IS NULL AND o.Alias IS NULL AND n.Name = o.Name))
    WHERE o.AppId = @sourceAppId AND o.IsDeleted = 0;

    IF (SELECT COUNT(DISTINCT NewTableId) FROM #pair) <> (SELECT COUNT(*) FROM #pair)
       OR (SELECT COUNT(*) FROM #pair) <> (SELECT COUNT(*) FROM meta.AppTable WHERE AppId = @newAppId AND IsDeleted = 0)
        THROW 50500, N'The copied tables could not be matched with the tables of the original app.', 1;

    -- Old record id -> new record id for every table, before any row is copied. New ids are 1..N in the original
    -- order (reports sorted by Record ID# keep their order), and a Reference value can be translated whichever table its
    -- parent is in, the child's own table and cycles included. Only rows live at this point are copied.
    CREATE TABLE #map (
        TableId BIGINT NOT NULL,   -- the copied table
        OldId   BIGINT NOT NULL,
        NewId   BIGINT NOT NULL,
        PRIMARY KEY (TableId, OldId)
    );

    DECLARE @oldTableId BIGINT, @newTableId BIGINT, @name NVARCHAR(200), @sql NVARCHAR(MAX), @n BIGINT,
            @tableRows BIGINT, @tableCopied BIGINT, @total BIGINT, @copied BIGINT = 0, @fromId BIGINT, @toId BIGINT,
            @cols NVARCHAR(MAX), @sel NVARCHAR(MAX), @joins NVARCHAR(MAX), @stage NVARCHAR(400);
    DECLARE @ref TABLE (Col SYSNAME NOT NULL PRIMARY KEY, ParentTableId BIGINT NOT NULL, Alias SYSNAME NOT NULL);
    DECLARE @file TABLE (Col SYSNAME NOT NULL PRIMARY KEY);

    BEGIN TRY
        UPDATE meta.AppCopyRun
        SET ProgressPercent = 3, Stage = N'Preparing record ids', LastProgressOn = SYSUTCDATETIME()
        WHERE Id = @RunId;

        DECLARE pair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT OldTableId, NewTableId FROM #pair ORDER BY OldTableId;
        OPEN pair_cursor;
        FETCH NEXT FROM pair_cursor INTO @oldTableId, @newTableId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @sql = N'INSERT INTO #map (TableId, OldId, NewId) SELECT @tableId, Id, ROW_NUMBER() OVER (ORDER BY Id) '
                     + N'FROM data.' + QUOTENAME(CONCAT(N't_', @oldTableId)) + N' WHERE IsDeleted = 0; SET @n = @@ROWCOUNT;';
            EXEC sys.sp_executesql @sql, N'@tableId BIGINT, @n BIGINT OUTPUT', @tableId = @newTableId, @n = @n OUTPUT;
            UPDATE #pair SET RowTotal = @n WHERE OldTableId = @oldTableId;
            UPDATE meta.AppCopyRun SET LastProgressOn = SYSUTCDATETIME() WHERE Id = @RunId;
            FETCH NEXT FROM pair_cursor INTO @oldTableId, @newTableId;
        END
        CLOSE pair_cursor;

        SET @total = (SELECT ISNULL(SUM(RowTotal), 0) FROM #pair);
        UPDATE meta.AppCopyRun
        SET TotalRows = @total, ProgressPercent = 5, Stage = N'Copying records', LastProgressOn = SYSUTCDATETIME()
        WHERE Id = @RunId;

        OPEN pair_cursor;
        FETCH NEXT FROM pair_cursor INTO @oldTableId, @newTableId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @name = Name, @tableRows = RowTotal FROM #pair WHERE OldTableId = @oldTableId;

            -- Reference columns of this table and the (copied) parent table whose ids they hold.
            DELETE @ref;
            INSERT INTO @ref (Col, ParentTableId, Alias)
            SELECT x.Col, x.ParentTableId, CONCAT(N'r', ROW_NUMBER() OVER (ORDER BY x.Col))
            FROM (SELECT f.PhysicalColumnName AS Col, MIN(r.ParentTableId) AS ParentTableId
                  FROM meta.Relationship r
                  JOIN meta.AppField f ON f.AppTableId = r.ChildTableId AND f.Fid = r.ReferenceFid AND f.IsDeleted = 0
                  WHERE r.AppId = @newAppId AND r.IsDeleted = 0 AND r.ChildTableId = @newTableId
                  GROUP BY f.PhysicalColumnName) x;

            DELETE @file;
            INSERT INTO @file (Col)
            SELECT DISTINCT f.PhysicalColumnName
            FROM meta.AppField f
            JOIN core.FieldType ft ON ft.Id = f.FieldTypeId
            WHERE f.AppTableId = @newTableId AND f.IsDeleted = 0 AND ft.Code = N'File' AND f.PhysicalColumnName IS NOT NULL;

            -- What each column of the copy gets: the new id, a new PublicId, the parent's new id, NULL for files left out,
            -- otherwise the value as it is (ciphertext of encrypted fields included: the copy has the same data key; File
            -- values get the copy's own paths in the Files stage). Computed and rowversion columns fill themselves.
            SELECT @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY c.column_id),
                   @sel  = STRING_AGG(CAST(CASE
                               WHEN c.name = N'Id' THEN N'm.NewId'
                               WHEN c.name = N'PublicId' THEN N'NEWID()'
                               WHEN rf.Alias IS NOT NULL THEN rf.Alias + N'.NewId'
                               WHEN fc.Col IS NOT NULL AND @includeFiles = 0 THEN N'NULL'
                               ELSE N'o.' + QUOTENAME(c.name)
                           END AS NVARCHAR(MAX)), N', ') WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns c
            LEFT JOIN @ref rf ON rf.Col = c.name
            LEFT JOIN @file fc ON fc.Col = c.name
            WHERE c.object_id = OBJECT_ID(N'data.' + QUOTENAME(CONCAT(N't_', @newTableId)))
              AND c.is_computed = 0 AND TYPE_NAME(c.system_type_id) <> N'timestamp';

            -- A reference whose parent is not copied (a deleted parent) becomes NULL: its old id could be another
            -- record's new id.
            SELECT @joins = ISNULL(STRING_AGG(CAST(CONCAT(N' LEFT JOIN #map ', Alias, N' ON ', Alias, N'.TableId = ', ParentTableId,
                                                          N' AND ', Alias, N'.OldId = o.', QUOTENAME(Col)) AS NVARCHAR(MAX)), N''), N'')
            FROM @ref;

            -- Rows deleted in the original since their id was mapped come along as deleted, so no reference points at a
            -- missing row.
            SET @sql = N'SET IDENTITY_INSERT data.' + QUOTENAME(CONCAT(N't_', @newTableId)) + N' ON; '
                     + N'INSERT INTO data.' + QUOTENAME(CONCAT(N't_', @newTableId)) + N' (' + @cols + N') '
                     + N'SELECT ' + @sel + N' FROM data.' + QUOTENAME(CONCAT(N't_', @oldTableId)) + N' o '
                     + N'JOIN #map m ON m.TableId = @tableId AND m.OldId = o.Id' + @joins
                     + N' WHERE o.Id > @fromId AND o.Id <= @toId; SET @n = @@ROWCOUNT; '
                     + N'SET IDENTITY_INSERT data.' + QUOTENAME(CONCAT(N't_', @newTableId)) + N' OFF;';

            -- Chunks of @ChunkSize rows: one INSERT for a whole big table is several times slower (measured on 1.5M rows:
            -- 378 s in one statement, 86 s in chunks of 100,000).
            SET @fromId = 0;
            SET @tableCopied = 0;
            WHILE 1 = 1
            BEGIN
                SET @toId = (SELECT MAX(OldId) FROM (SELECT TOP (@ChunkSize) OldId FROM #map
                                                     WHERE TableId = @newTableId AND OldId > @fromId ORDER BY OldId) x);
                IF @toId IS NULL BREAK;

                IF @ownTran = 1 BEGIN TRANSACTION;
                EXEC sys.sp_executesql @sql, N'@tableId BIGINT, @fromId BIGINT, @toId BIGINT, @n BIGINT OUTPUT',
                     @tableId = @newTableId, @fromId = @fromId, @toId = @toId, @n = @n OUTPUT;
                SET @tableCopied += @n;
                SET @copied += @n;
                SET @stage = CONCAT(N'Copying records: ', @name, N' ', FORMAT(@tableCopied, 'N0', 'en-US'), N' of ',
                                    FORMAT(@tableRows, 'N0', 'en-US'));
                UPDATE meta.AppCopyRun
                SET RowsCopied = @copied, ProgressPercent = 5 + CAST((@RowsEndPercent - 5) * @copied / NULLIF(@total, 0) AS INT),
                    Stage = @stage, LastProgressOn = SYSUTCDATETIME()
                WHERE Id = @RunId;
                IF @ownTran = 1 COMMIT TRANSACTION;

                SET @fromId = @toId;
            END

            -- New records in the copy continue after the last copied id; RecordCount as the tables list shows it.
            IF @tableRows > 0
            BEGIN
                SET @sql = CONCAT(N'DBCC CHECKIDENT (N''data.t_', @newTableId, N''', RESEED, ', @tableRows, N') WITH NO_INFOMSGS;');
                EXEC sys.sp_executesql @sql;
            END
            SET @sql = N'SELECT @n = COUNT_BIG(*) FROM data.' + QUOTENAME(CONCAT(N't_', @newTableId)) + N' WHERE IsDeleted = 0;';
            EXEC sys.sp_executesql @sql, N'@n BIGINT OUTPUT', @n = @n OUTPUT;
            UPDATE meta.AppTable SET RecordCount = CAST(@n AS INT) WHERE Id = @newTableId;

            FETCH NEXT FROM pair_cursor INTO @oldTableId, @newTableId;
        END
        CLOSE pair_cursor;
        DEALLOCATE pair_cursor;

        UPDATE meta.AppCopyRun
        SET ProgressPercent = @RowsEndPercent,
            Stage = IIF(@RowsEndPercent < 97, N'Copying file attachments', N'Finishing'),
            LastProgressOn = SYSUTCDATETIME()
        WHERE Id = @RunId;
    END TRY
    BEGIN CATCH
        IF @ownTran = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    -- For the caller: rows copied per table (search reindex) and in all.
    SELECT NewTableId, RowTotal FROM #pair ORDER BY OldTableId;
END
GO

-- Copies left behind when the server restarted in the middle (their progress stopped moving). Called before a new copy
-- starts, instead of a background job. Returns the PublicIds of the copies it removed: the caller deletes their files.
CREATE OR ALTER PROCEDURE meta.usp_DiscardStaleAppCopies
    @StaleMinutes INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @runId BIGINT;
    DECLARE @discarded TABLE (RunId BIGINT NOT NULL PRIMARY KEY);
    DECLARE stale_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT Id FROM meta.AppCopyRun
        WHERE Status = 'Running' AND LastProgressOn < DATEADD(MINUTE, -@StaleMinutes, SYSUTCDATETIME());
    OPEN stale_cursor;
    FETCH NEXT FROM stale_cursor INTO @runId;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC meta.usp_DiscardAppCopy @RunId = @runId, @Error = N'The copy stopped before it finished (the server restarted) and was removed.';
        INSERT INTO @discarded (RunId) VALUES (@runId);
        FETCH NEXT FROM stale_cursor INTO @runId;
    END
    CLOSE stale_cursor;
    DEALLOCATE stale_cursor;

    SELECT r.NewAppPublicId
    FROM meta.AppCopyRun r
    JOIN @discarded d ON d.RunId = r.Id
    WHERE r.Status = 'Failed';
END
GO

IF OBJECT_ID(N'meta.usp_RegisterSharedFileBlobs', N'P') IS NOT NULL
    DROP PROCEDURE meta.usp_RegisterSharedFileBlobs;
GO

-- Only ever written by copies made with migration 091's sharing, which existed on developers' machines only.
IF OBJECT_ID(N'meta.SharedFileBlob', N'U') IS NOT NULL
    DROP TABLE meta.SharedFileBlob;
GO
