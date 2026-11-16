-- Copy App – Schema + Data (client document "App Copy & Import Capabilities", 1.2; plan: PLAN_Copy_App_Schema_Data.md).
-- The structure is copied first by meta.usp_CopyAppSchema (migration 090) into an app with Status 'Draft', which nobody's
-- apps list shows. The caller then runs these procedures in the same API request (no background job):
--
--   meta.usp_CopyAppData     the records, in chunks of their own transaction: new record ids (1..N in the original
--                            order), Reference values translated to the copy's ids, File values kept (blobs shared) or
--                            cleared. Writes its progress to meta.AppCopyRun after every chunk.
--   meta.usp_FinishAppCopy   makes the copy Active.
--   meta.usp_DiscardAppCopy  removes a copy that failed; meta.usp_DiscardStaleAppCopies removes copies left behind by a
--                            server restart (no progress for a while).
--
--   meta.AppCopyRun          one row per copy with data: progress for the dialog (readable by any API instance, also when
--                            the HTTP request has been cut after the hosting timeout), result, error.
--   meta.SharedFileBlob      file blobs that more than one app points at since a copy with data. A blob is deleted only
--                            when the last app lets go of it (FileReservationService).
--
-- Error numbers: 50404 run not found / not running, 50500 tables could not be matched.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'meta.AppCopyRun', N'U') IS NULL
BEGIN
    CREATE TABLE meta.AppCopyRun (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppCopyRun PRIMARY KEY,
        ProgressId      UNIQUEIDENTIFIER NULL,
        SourceAppId     BIGINT           NOT NULL,
        NewAppId        BIGINT           NOT NULL,
        NewAppPublicId  UNIQUEIDENTIFIER NOT NULL,
        StartedBy       BIGINT           NOT NULL,
        StartedOn       DATETIME2(7)     NOT NULL CONSTRAINT DF_AppCopyRun_StartedOn DEFAULT SYSUTCDATETIME(),
        IncludeFiles    BIT              NOT NULL,
        -- Running, Done or Failed.
        Status          VARCHAR(20)      NOT NULL CONSTRAINT DF_AppCopyRun_Status DEFAULT 'Running',
        ProgressPercent INT              NOT NULL CONSTRAINT DF_AppCopyRun_ProgressPercent DEFAULT 0,
        Stage           NVARCHAR(400)    NOT NULL CONSTRAINT DF_AppCopyRun_Stage DEFAULT N'',
        RowsCopied      BIGINT           NOT NULL CONSTRAINT DF_AppCopyRun_RowsCopied DEFAULT 0,
        TotalRows       BIGINT           NOT NULL CONSTRAINT DF_AppCopyRun_TotalRows DEFAULT 0,
        -- Moves on with every chunk; a Running copy that stops moving was cut off by a restart.
        LastProgressOn  DATETIME2(7)     NOT NULL CONSTRAINT DF_AppCopyRun_LastProgressOn DEFAULT SYSUTCDATETIME(),
        FinishedOn      DATETIME2(7)     NULL,
        WarningsJson    NVARCHAR(MAX)    NULL,
        Error           NVARCHAR(2000)   NULL
    );
    CREATE UNIQUE INDEX UX_AppCopyRun_ProgressId ON meta.AppCopyRun (ProgressId) WHERE ProgressId IS NOT NULL;
    CREATE INDEX IX_AppCopyRun_Status ON meta.AppCopyRun (Status, LastProgressOn);
END
GO

IF OBJECT_ID(N'meta.SharedFileBlob', N'U') IS NULL
BEGIN
    CREATE TABLE meta.SharedFileBlob (
        Path      NVARCHAR(4000) NOT NULL,
        -- Paths can be longer than an index key allows, so the key is their hash.
        PathHash  AS CAST(HASHBYTES('SHA2_256', Path) AS BINARY(32)) PERSISTED NOT NULL CONSTRAINT PK_SharedFileBlob PRIMARY KEY,
        -- How many apps point at the blob (2 after the first copy).
        RefCount  INT            NOT NULL,
        CreatedOn DATETIME2(7)   NOT NULL CONSTRAINT DF_SharedFileBlob_CreatedOn DEFAULT SYSUTCDATETIME()
    );
END
GO

CREATE OR ALTER PROCEDURE meta.usp_CopyAppData
    @RunId     BIGINT,
    @ChunkSize INT = 100000
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
            @cols NVARCHAR(MAX), @sel NVARCHAR(MAX), @joins NVARCHAR(MAX), @fileCols NVARCHAR(MAX), @stage NVARCHAR(400);
    DECLARE @ref TABLE (Col SYSNAME NOT NULL PRIMARY KEY, ParentTableId BIGINT NOT NULL, Alias SYSNAME NOT NULL);
    DECLARE @file TABLE (Col SYSNAME NOT NULL PRIMARY KEY, IsEncrypted BIT NOT NULL);
    CREATE TABLE #path (Path NVARCHAR(4000) NOT NULL);

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
            INSERT INTO @file (Col, IsEncrypted)
            SELECT f.PhysicalColumnName, MAX(CAST(f.IsEncrypted AS INT))
            FROM meta.AppField f
            JOIN core.FieldType ft ON ft.Id = f.FieldTypeId
            WHERE f.AppTableId = @newTableId AND f.IsDeleted = 0 AND ft.Code = N'File' AND f.PhysicalColumnName IS NOT NULL
            GROUP BY f.PhysicalColumnName;

            -- What each column of the copy gets: the new id, a new PublicId, the parent's new id, NULL for files left out,
            -- otherwise the value as it is (ciphertext of encrypted fields included: the copy has the same data key).
            -- Computed and rowversion columns fill themselves.
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
                SET RowsCopied = @copied, ProgressPercent = 5 + CAST(92 * @copied / NULLIF(@total, 0) AS INT),
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

            -- Blobs the copy now shares with the original (plain File values; the caller adds encrypted ones). A value is
            -- one attachment or an array of them; each has a path and older revisions with their own paths.
            IF @includeFiles = 1 AND @tableRows > 0
            BEGIN
                SET @fileCols = NULL;
                SELECT @fileCols = STRING_AGG(CAST(
                           N'INSERT INTO #path (Path) SELECT p.Path FROM data.' + QUOTENAME(CONCAT(N't_', @newTableId)) + N' t '
                         + N'CROSS APPLY (SELECT CASE WHEN ISJSON(t.' + QUOTENAME(Col) + N') = 1 THEN IIF(LEFT(LTRIM(t.' + QUOTENAME(Col)
                         + N'), 1) = N''['', t.' + QUOTENAME(Col) + N', N''['' + t.' + QUOTENAME(Col) + N' + N'']'') END AS Arr) a '
                         + N'CROSS APPLY OPENJSON(a.Arr) e '
                         + N'CROSS APPLY (SELECT JSON_VALUE(IIF(e.type = 5, e.value, NULL), ''$.path'') AS Path '
                         + N'UNION ALL SELECT JSON_VALUE(IIF(r.type = 5, r.value, NULL), ''$.path'') '
                         + N'FROM OPENJSON(IIF(e.type = 5, e.value, NULL), ''$.revisions'') r) p '
                         + N'WHERE t.' + QUOTENAME(Col) + N' IS NOT NULL AND p.Path IS NOT NULL AND LEN(p.Path) > 0;'
                       AS NVARCHAR(MAX)), NCHAR(10))
                FROM @file
                WHERE IsEncrypted = 0;
                IF @fileCols IS NOT NULL EXEC sys.sp_executesql @fileCols;
            END

            FETCH NEXT FROM pair_cursor INTO @oldTableId, @newTableId;
        END
        CLOSE pair_cursor;
        DEALLOCATE pair_cursor;

        IF EXISTS (SELECT 1 FROM #path)
        BEGIN
            UPDATE meta.AppCopyRun SET Stage = N'Linking file attachments', LastProgressOn = SYSUTCDATETIME() WHERE Id = @RunId;
            -- Same counting as meta.usp_RegisterSharedFileBlobs: a path seen for the first time is now in 2 apps.
            MERGE meta.SharedFileBlob WITH (HOLDLOCK) AS t
            USING (SELECT DISTINCT Path FROM #path) AS s
            ON t.PathHash = CAST(HASHBYTES('SHA2_256', s.Path) AS BINARY(32))
            WHEN MATCHED THEN UPDATE SET RefCount = t.RefCount + 1
            WHEN NOT MATCHED THEN INSERT (Path, RefCount) VALUES (s.Path, 2);
        END

        UPDATE meta.AppCopyRun
        SET ProgressPercent = 97, Stage = N'Finishing', LastProgressOn = SYSUTCDATETIME()
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

-- Paths of encrypted File values, which SQL cannot read: the caller decrypts them and registers them here.
-- @PathsJson: JSON array of blob paths that one more app now points at.
CREATE OR ALTER PROCEDURE meta.usp_RegisterSharedFileBlobs
    @PathsJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    -- Same counting as the end of meta.usp_CopyAppData.
    MERGE meta.SharedFileBlob WITH (HOLDLOCK) AS t
    USING (SELECT DISTINCT CAST(j.value AS NVARCHAR(4000)) AS Path
           FROM OPENJSON(@PathsJson) j
           WHERE j.type = 1 AND LEN(j.value) > 0) AS s
    ON t.PathHash = CAST(HASHBYTES('SHA2_256', s.Path) AS BINARY(32))
    WHEN MATCHED THEN UPDATE SET RefCount = t.RefCount + 1
    WHEN NOT MATCHED THEN INSERT (Path, RefCount) VALUES (s.Path, 2);
END
GO

CREATE OR ALTER PROCEDURE meta.usp_FinishAppCopy
    @RunId        BIGINT,
    @WarningsJson NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ownTran BIT = IIF(@@TRANCOUNT = 0, 1, 0);

    IF @ownTran = 1 BEGIN TRANSACTION;
    UPDATE a SET Status = 'Active'
    FROM meta.App a
    JOIN meta.AppCopyRun r ON r.NewAppId = a.Id
    WHERE r.Id = @RunId AND r.Status = 'Running' AND a.Status = 'Draft' AND a.IsDeleted = 0;
    IF @@ROWCOUNT = 0
    BEGIN
        IF @ownTran = 1 ROLLBACK TRANSACTION;
        THROW 50404, N'The copy was not found or is not running.', 1;
    END

    UPDATE meta.AppCopyRun
    SET Status = 'Done', ProgressPercent = 100, Stage = N'Done', WarningsJson = @WarningsJson,
        FinishedOn = SYSUTCDATETIME(), LastProgressOn = SYSUTCDATETIME()
    WHERE Id = @RunId;
    IF @ownTran = 1 COMMIT TRANSACTION;
END
GO

-- Removes the copy of a run that failed (or was cut off): drops its record tables, which may hold millions of rows, and
-- retires the app the way a deleted app is. Works only on a Draft app of a run that did not finish; the original is never
-- touched. Blob registrations of the run stay: a shared blob is then kept longer, never deleted too early.
CREATE OR ALTER PROCEDURE meta.usp_DiscardAppCopy
    @RunId  BIGINT,
    @Error  NVARCHAR(2000) = NULL,
    @UserId BIGINT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @newAppId BIGINT, @now DATETIME2(7) = SYSUTCDATETIME(), @tableId BIGINT, @sql NVARCHAR(MAX);
    SELECT @newAppId = r.NewAppId
    FROM meta.AppCopyRun r
    JOIN meta.App a ON a.Id = r.NewAppId
    WHERE r.Id = @RunId AND r.Status IN ('Running', 'Failed') AND a.Status = 'Draft';
    IF @newAppId IS NULL RETURN;

    DECLARE @ownTran BIT = IIF(@@TRANCOUNT = 0, 1, 0);
    BEGIN TRY
        IF @ownTran = 1 BEGIN TRANSACTION;

        DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM meta.AppTable WHERE AppId = @newAppId;
        OPEN table_cursor;
        FETCH NEXT FROM table_cursor INTO @tableId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF OBJECT_ID(N'data.' + QUOTENAME(CONCAT(N't_', @tableId)), N'U') IS NOT NULL
            BEGIN
                SET @sql = N'DROP TABLE data.' + QUOTENAME(CONCAT(N't_', @tableId)) + N';';
                EXEC sys.sp_executesql @sql;
            END
            FETCH NEXT FROM table_cursor INTO @tableId;
        END
        CLOSE table_cursor;
        DEALLOCATE table_cursor;

        UPDATE meta.AppTable SET IsDeleted = 1, DeletedOn = @now, DeletedBy = @UserId WHERE AppId = @newAppId AND IsDeleted = 0;
        UPDATE meta.App SET IsDeleted = 1, DeletedOn = @now, DeletedBy = @UserId WHERE Id = @newAppId AND IsDeleted = 0;
        UPDATE meta.AppCopyRun
        SET Status = 'Failed', Error = COALESCE(@Error, Error), FinishedOn = @now, LastProgressOn = @now
        WHERE Id = @RunId;

        IF @ownTran = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @ownTran = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

-- Copies left behind when the server restarted in the middle (their progress stopped moving). Called before a new copy
-- starts, instead of a background job.
CREATE OR ALTER PROCEDURE meta.usp_DiscardStaleAppCopies
    @StaleMinutes INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @runId BIGINT;
    DECLARE stale_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT Id FROM meta.AppCopyRun
        WHERE Status = 'Running' AND LastProgressOn < DATEADD(MINUTE, -@StaleMinutes, SYSUTCDATETIME());
    OPEN stale_cursor;
    FETCH NEXT FROM stale_cursor INTO @runId;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC meta.usp_DiscardAppCopy @RunId = @runId, @Error = N'The copy stopped before it finished (the server restarted) and was removed.';
        FETCH NEXT FROM stale_cursor INTO @runId;
    END
    CLOSE stale_cursor;
    DEALLOCATE stale_cursor;
END
GO
