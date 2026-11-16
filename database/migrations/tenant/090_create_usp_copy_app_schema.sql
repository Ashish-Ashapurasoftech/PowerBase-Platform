-- Copy App – Schema Only (client document "App Copy & Import Capabilities", 1.1; plan: PLAN_Copy_App_Schema_Only.md).
-- One procedure, one transaction: the new app with the same tables, fields (same Fids), relationships, forms, rules,
-- reports, roles, pages and saved imports, and NO records. A failure rolls everything back, CREATE TABLE included.
--
-- PowerFlows are not copied (decided 2026-11-03). A PowerFlow belongs to the person who built it and the PowerFlows list
-- shows all of that person's PowerFlows from every app, so a copy would show every PowerFlow twice in that list.
--
-- Called by the CopyApp command after its permission and name checks. The caller writes the new app's DEK when the app is
-- encrypted (the key-encryption key is not reachable from SQL): the procedure only clears the copied WrappedDek.
--
-- Result sets: 1) warnings (Code, Message) for the dialog, 2) time per stage (Stage, Ms).
-- Progress: informational messages (severity 0, sent at once WITH NOWAIT) "copy-progress:<percent>:<stage>", which the
-- caller reads for the dialog's progress bar. They change nothing; the percentages follow each stage's share of the time
-- a 50,000-field copy took. Message texts must never contain a percent sign (RAISERROR reads it as a format).
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE meta.usp_CopyAppSchema
    @SourceAppId    BIGINT,
    @NewName        NVARCHAR(200),
    @NewDescription NVARCHAR(MAX)    = NULL,
    @UserId         BIGINT,
    @UserPublicId   UNIQUEIDENTIFIER = NULL,
    @UserName       NVARCHAR(256)    = NULL,
    @UserEmail      NVARCHAR(256)    = NULL,
    -- 0 = "Only me" (default): only the copier gets access. 1 = "Copy users in their roles".
    @CopyUsers      BIT              = 0,
    @NewAppId       BIGINT           = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now        DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @stageStart DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ownTran    BIT          = IIF(@@TRANCOUNT = 0, 1, 0);

    IF NOT EXISTS (SELECT 1 FROM meta.App WHERE Id = @SourceAppId AND IsDeleted = 0)
        THROW 50404, N'The app to copy was not found.', 1;
    IF EXISTS (SELECT 1 FROM meta.App WHERE Name = @NewName AND IsDeleted = 0)
        THROW 50409, N'An app with this name already exists.', 1;

    -- Old id -> new id for every copied row that something else points at. PublicIds are GUIDs, so one lookup on
    -- OldPublicId is enough to rewrite JSON (see the JSON stage).
    CREATE TABLE #IdMap (
        Entity      VARCHAR(30)      NOT NULL,
        OldId       BIGINT           NOT NULL,
        NewId       BIGINT           NOT NULL,
        OldPublicId UNIQUEIDENTIFIER NULL,
        NewPublicId UNIQUEIDENTIFIER NULL,
        PRIMARY KEY (Entity, OldId)
    );
    CREATE INDEX IX_IdMap_OldPublicId ON #IdMap (OldPublicId) INCLUDE (NewPublicId);

    CREATE TABLE #Warning (Code VARCHAR(40) NOT NULL, Message NVARCHAR(1000) NOT NULL);
    CREATE TABLE #Timing  (Stage VARCHAR(40) NOT NULL, Ms INT NOT NULL);
    CREATE TABLE #Json (
        Tbl     VARCHAR(60)   NOT NULL,
        RowId   BIGINT        NOT NULL,
        Txt     NVARCHAR(MAX) NOT NULL,
        NewTxt  NVARCHAR(MAX) NULL,
        Changed BIT           NOT NULL DEFAULT 0
    );

    BEGIN TRY
        IF @ownTran = 1 BEGIN TRANSACTION;
        RAISERROR(N'copy-progress:1:Starting', 0, 1) WITH NOWAIT;

        -- ── App ─────────────────────────────────────────────────────────────────────────────────────────────
        DECLARE @newApp TABLE (Id BIGINT NOT NULL, PublicId UNIQUEIDENTIFIER NOT NULL);

        INSERT INTO meta.App (OwnerId, OwnerName, Name, Description, Icon, Color, Formatting, SecurityOptions,
                              Branding, LayoutSettings, IsEncrypted, CreatedOn, CreatedBy)
        OUTPUT inserted.Id, inserted.PublicId INTO @newApp (Id, PublicId)
        SELECT @UserId, @UserName, @NewName, @NewDescription, a.Icon, a.Color, a.Formatting,
               -- The copy gets its own data key, written by the caller.
               CASE WHEN ISJSON(a.SecurityOptions) = 1
                    THEN JSON_MODIFY(JSON_MODIFY(a.SecurityOptions, '$.WrappedDek', NULL), '$.wrappedDek', NULL)
                    ELSE a.SecurityOptions END,
               a.Branding, a.LayoutSettings, a.IsEncrypted, @now, @UserId
        FROM meta.App a
        WHERE a.Id = @SourceAppId;

        SELECT @NewAppId = Id FROM @newApp;

        INSERT INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId)
        SELECT 'App', a.Id, n.Id, a.PublicId, n.PublicId
        FROM meta.App a CROSS JOIN @newApp n
        WHERE a.Id = @SourceAppId;

        INSERT INTO meta.AppVariable (AppId, Name, Value, Description, CreatedOn, CreatedBy)
        SELECT @NewAppId, v.Name, v.Value, v.Description, @now, @UserId
        FROM meta.AppVariable v
        WHERE v.AppId = @SourceAppId AND v.IsDeleted = 0;

        -- ── Tables and fields ───────────────────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:2:Copying tables and fields', 0, 1) WITH NOWAIT;
        MERGE meta.AppTable AS t
        USING (SELECT Id, PublicId, Name, SingularLabel, PluralLabel, Description, DefaultReportSettings, IsSystem,
                      DisplayOrder, Icon, IsShowInBar, Alias, CustomDataRule, IsCustomDataRuleEnabled, IsAutoSaveOnLeave
               FROM meta.AppTable
               WHERE AppId = @SourceAppId AND IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppId, Name, SingularLabel, PluralLabel, Description, DefaultReportSettings, RecordCount, IsSystem,
                    DisplayOrder, Icon, CreatedOn, CreatedBy, IsShowInBar, Alias, CustomDataRule,
                    IsCustomDataRuleEnabled, IsAutoSaveOnLeave)
            VALUES (@NewAppId, s.Name, s.SingularLabel, s.PluralLabel, s.Description, s.DefaultReportSettings, 0, s.IsSystem,
                    s.DisplayOrder, s.Icon, @now, @UserId, s.IsShowInBar, s.Alias, s.CustomDataRule,
                    s.IsCustomDataRuleEnabled, s.IsAutoSaveOnLeave)
        OUTPUT 'AppTable', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        UPDATE t SET PhysicalTableName = CONCAT(N't_', t.Id)
        FROM meta.AppTable t
        JOIN #IdMap m ON m.Entity = 'AppTable' AND m.NewId = t.Id;

        -- Live fields keep their Fid, so every Fid stored in reports, forms, rules and filters stays valid.
        -- GetNextFid uses MAX(Fid) over deleted fields too: when a deleted field holds the highest Fid of its table,
        -- it is copied as a deleted "tombstone" so a new field in the copy cannot take a Fid an old report still uses.
        WITH tomb AS (
            SELECT d.Id
            FROM (SELECT f.Id, f.AppTableId, f.Fid,
                         ROW_NUMBER() OVER (PARTITION BY f.AppTableId ORDER BY f.Fid DESC, f.Id DESC) AS rn
                  FROM meta.AppField f
                  JOIN #IdMap m ON m.Entity = 'AppTable' AND m.OldId = f.AppTableId
                  WHERE f.IsDeleted = 1 AND f.Fid IS NOT NULL) d
            WHERE d.rn = 1
              AND d.Fid > ISNULL((SELECT MAX(l.Fid) FROM meta.AppField l
                                  WHERE l.AppTableId = d.AppTableId AND l.IsDeleted = 0), 0)
        )
        MERGE meta.AppField AS t
        USING (SELECT f.Id, f.PublicId, m.NewId AS NewTableId, f.FieldTypeId, f.Name, f.Label, f.HelpText, f.Placeholder,
                      f.Description, f.PhysicalColumnName, f.DefaultValue, f.MaxLength, f.Precision, f.Scale,
                      f.DisplayOrder, f.IsRequired, f.IsUnique, f.IsPrimary, f.IsSystem, f.IsSearchable, f.IsSortable,
                      f.IsFilterable, f.IsReportable, f.Settings, f.IsDeleted, f.DeletedOn, f.DeletedBy, f.Fid,
                      f.IsAuditable, f.IsEncrypted, f.IsAutoFill, f.IsHidden, f.SearchSlot, f.SearchSlotEnd,
                      f.SnapshotSourceFid, f.SnapshotEditable, f.IsSharedValues
               FROM meta.AppField f
               JOIN #IdMap m ON m.Entity = 'AppTable' AND m.OldId = f.AppTableId
               WHERE f.IsDeleted = 0 OR f.Id IN (SELECT Id FROM tomb)) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppTableId, FieldTypeId, Name, Label, HelpText, Placeholder, Description, PhysicalColumnName,
                    DefaultValue, MaxLength, Precision, Scale, DisplayOrder, IsRequired, IsUnique, IsPrimary, IsSystem,
                    IsSearchable, IsSortable, IsFilterable, IsReportable, Settings, IsDeleted, CreatedOn, CreatedBy,
                    DeletedOn, DeletedBy, Fid, IsAuditable, IsEncrypted, IsAutoFill, IsHidden, SearchSlot,
                    SearchSlotEnd, SnapshotSourceFid, SnapshotEditable, IsSharedValues)
            VALUES (s.NewTableId, s.FieldTypeId, s.Name, s.Label, s.HelpText, s.Placeholder, s.Description,
                    s.PhysicalColumnName, s.DefaultValue, s.MaxLength, s.Precision, s.Scale, s.DisplayOrder,
                    s.IsRequired, s.IsUnique, s.IsPrimary, s.IsSystem, s.IsSearchable, s.IsSortable, s.IsFilterable,
                    s.IsReportable, IIF(s.IsDeleted = 1, NULL, s.Settings), s.IsDeleted, @now, @UserId,
                    s.DeletedOn, s.DeletedBy, s.Fid, s.IsAuditable, s.IsEncrypted, s.IsAutoFill, s.IsHidden,
                    IIF(s.IsDeleted = 1, NULL, s.SearchSlot), IIF(s.IsDeleted = 1, NULL, s.SearchSlotEnd),
                    s.SnapshotSourceFid, s.SnapshotEditable, s.IsSharedValues)
        OUTPUT 'AppField', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        -- These columns hold AppField.Id (not the Fid).
        UPDATE t SET
            DisplayFieldId              = fd.NewId,
            KeyFieldId                  = fk.NewId,
            DefaultRecordPickerField1Id = f1.NewId,
            DefaultRecordPickerField2Id = f2.NewId,
            DefaultRecordPickerField3Id = f3.NewId
        FROM meta.AppTable t
        JOIN #IdMap mt ON mt.Entity = 'AppTable' AND mt.NewId = t.Id
        JOIN meta.AppTable s ON s.Id = mt.OldId
        LEFT JOIN #IdMap fd ON fd.Entity = 'AppField' AND fd.OldId = s.DisplayFieldId
        LEFT JOIN #IdMap fk ON fk.Entity = 'AppField' AND fk.OldId = s.KeyFieldId
        LEFT JOIN #IdMap f1 ON f1.Entity = 'AppField' AND f1.OldId = s.DefaultRecordPickerField1Id
        LEFT JOIN #IdMap f2 ON f2.Entity = 'AppField' AND f2.OldId = s.DefaultRecordPickerField2Id
        LEFT JOIN #IdMap f3 ON f3.Entity = 'AppField' AND f3.OldId = s.DefaultRecordPickerField3Id;

        MERGE meta.Relationship AS t
        USING (SELECT r.Id, r.PublicId, mp.NewId AS ParentTableId, mc.NewId AS ChildTableId,
                      mr.NewId AS ReferenceFieldId, r.ReferenceFid, mx.NewId AS ProxyFieldId,
                      r.ReferenceFieldIsExisting, mk.NewId AS DisplayKeyFieldId
               FROM meta.Relationship r
               JOIN #IdMap mp ON mp.Entity = 'AppTable' AND mp.OldId = r.ParentTableId
               JOIN #IdMap mc ON mc.Entity = 'AppTable' AND mc.OldId = r.ChildTableId
               JOIN #IdMap mr ON mr.Entity = 'AppField' AND mr.OldId = r.ReferenceFieldId
               LEFT JOIN #IdMap mx ON mx.Entity = 'AppField' AND mx.OldId = r.ProxyFieldId
               LEFT JOIN #IdMap mk ON mk.Entity = 'AppField' AND mk.OldId = r.DisplayKeyFieldId
               WHERE r.AppId = @SourceAppId AND r.IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppId, ParentTableId, ChildTableId, ReferenceFieldId, ReferenceFid, ProxyFieldId, CreatedOn,
                    CreatedBy, ReferenceFieldIsExisting, DisplayKeyFieldId)
            VALUES (@NewAppId, s.ParentTableId, s.ChildTableId, s.ReferenceFieldId, s.ReferenceFid, s.ProxyFieldId,
                    @now, @UserId, s.ReferenceFieldIsExisting, s.DisplayKeyFieldId)
        OUTPUT 'Relationship', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        INSERT INTO #Timing VALUES ('tables_fields', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── data.t_{new}: cloned from the source table's own definition ────────────────────────────────────
        -- Same columns, types, collations, encryption, identity, defaults, computed columns (f_X_unique_hash) and
        -- indexes as the source, so the field-type -> SQL-type rules of SchemaEngineService are not repeated here.
        DECLARE @oldTableId BIGINT, @newTableId BIGINT, @srcObject INT, @dstName SYSNAME,
                @oldTok NVARCHAR(30), @newTok NVARCHAR(30), @cols NVARCHAR(MAX), @pk NVARCHAR(MAX),
                @idx NVARCHAR(MAX), @ddl NVARCHAR(MAX), @msg NVARCHAR(1000), @progress NVARCHAR(200),
                @tableNo INT = 0, @tableCount INT = (SELECT COUNT(*) FROM #IdMap WHERE Entity = 'AppTable');

        SET @progress = CONCAT(N'copy-progress:13:Creating tables 0 of ', @tableCount);
        RAISERROR(@progress, 0, 1) WITH NOWAIT;
        DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT OldId, NewId FROM #IdMap WHERE Entity = 'AppTable' ORDER BY OldId;
        OPEN table_cursor;
        FETCH NEXT FROM table_cursor INTO @oldTableId, @newTableId;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @srcObject = OBJECT_ID(N'data.' + QUOTENAME(CONCAT(N't_', @oldTableId))),
                   @dstName   = CONCAT(N't_', @newTableId),
                   @oldTok    = CONCAT(N'_t_', @oldTableId),
                   @newTok    = CONCAT(N'_t_', @newTableId),
                   @cols = NULL, @pk = NULL, @idx = NULL;

            IF @srcObject IS NULL
            BEGIN
                SELECT @msg = CONCAT(N'Table "', Name, N'" has no physical table data.t_', @oldTableId, N'.')
                FROM meta.AppTable WHERE Id = @oldTableId;
                THROW 50500, @msg, 1;
            END

            SELECT @cols = STRING_AGG(CAST(
                       QUOTENAME(c.name) + N' ' +
                       CASE WHEN c.is_computed = 1
                            THEN N'AS ' + cc.definition + IIF(cc.is_persisted = 1, N' PERSISTED', N'')
                            ELSE
                                CASE
                                    WHEN ty.name = N'timestamp' THEN N'ROWVERSION'
                                    WHEN ty.name IN (N'varchar', N'char', N'varbinary', N'binary')
                                        THEN ty.name + N'(' + IIF(c.max_length = -1, N'MAX', CAST(c.max_length AS NVARCHAR(10))) + N')'
                                    WHEN ty.name IN (N'nvarchar', N'nchar')
                                        THEN ty.name + N'(' + IIF(c.max_length = -1, N'MAX', CAST(c.max_length / 2 AS NVARCHAR(10))) + N')'
                                    WHEN ty.name IN (N'decimal', N'numeric')
                                        THEN ty.name + N'(' + CAST(c.precision AS NVARCHAR(10)) + N',' + CAST(c.scale AS NVARCHAR(10)) + N')'
                                    WHEN ty.name IN (N'datetime2', N'time', N'datetimeoffset')
                                        THEN ty.name + N'(' + CAST(c.scale AS NVARCHAR(10)) + N')'
                                    ELSE ty.name
                                END
                                + ISNULL(N' COLLATE ' + c.collation_name, N'')
                                + ISNULL(N' ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = ' + QUOTENAME(cek.name)
                                         + N', ENCRYPTION_TYPE = ' + c.encryption_type_desc
                                         + N', ALGORITHM = ''' + c.encryption_algorithm_name + N''')', N'')
                                + ISNULL(N' IDENTITY(' + CAST(ic.seed_value AS NVARCHAR(40)) + N','
                                         + CAST(ic.increment_value AS NVARCHAR(40)) + N')', N'')
                                + IIF(c.is_nullable = 1, N' NULL', N' NOT NULL')
                                + ISNULL(N' DEFAULT ' + dc.definition, N'')
                       END AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, N',' + NCHAR(10)) WITHIN GROUP (ORDER BY c.column_id)
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.column_encryption_keys cek ON cek.column_encryption_key_id = c.column_encryption_key_id
            WHERE c.object_id = @srcObject;

            SELECT @pk = N'CONSTRAINT ' + QUOTENAME(N'PK_' + @dstName) + N' PRIMARY KEY ' + i.type_desc COLLATE DATABASE_DEFAULT
                         + N' (' + STRING_AGG(QUOTENAME(c.name) + IIF(ic.is_descending_key = 1, N' DESC', N''), N', ')
                                   WITHIN GROUP (ORDER BY ic.key_ordinal) + N')'
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @srcObject AND i.is_primary_key = 1
            GROUP BY i.type_desc;

            SET @ddl = N'CREATE TABLE data.' + QUOTENAME(@dstName) + N' (' + NCHAR(10) + @cols
                     + ISNULL(N',' + NCHAR(10) + @pk, N'') + N');';
            EXEC sys.sp_executesql @ddl;

            -- Index names only need to be unique per table; t_{old} in the name becomes t_{new} for readability.
            SELECT @idx = STRING_AGG(CAST(
                       N'CREATE ' + IIF(i.is_unique = 1, N'UNIQUE ', N'') + i.type_desc COLLATE DATABASE_DEFAULT + N' INDEX '
                       + QUOTENAME(LEFT(n.renamed, LEN(n.renamed) - 1)) + N' ON data.' + QUOTENAME(@dstName)
                       + N' (' + k.cols + N')' + ISNULL(N' INCLUDE (' + inc.cols + N')', N'')
                       + ISNULL(N' WHERE ' + i.filter_definition, N'') + N';' AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT, NCHAR(10))
            FROM sys.indexes i
            CROSS APPLY (SELECT REPLACE(REPLACE(i.name + N'|', @oldTok + N'_', @newTok + N'_'),
                                        @oldTok + N'|', @newTok + N'|') AS renamed) n
            CROSS APPLY (SELECT STRING_AGG(QUOTENAME(c.name) + IIF(ic.is_descending_key = 1, N' DESC', N''), N', ')
                                    WITHIN GROUP (ORDER BY ic.key_ordinal) AS cols
                         FROM sys.index_columns ic
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) k
            OUTER APPLY (SELECT STRING_AGG(QUOTENAME(c.name), N', ') WITHIN GROUP (ORDER BY ic.index_column_id) AS cols
                         FROM sys.index_columns ic
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1) inc
            WHERE i.object_id = @srcObject AND i.is_primary_key = 0 AND i.type IN (1, 2) AND i.is_hypothetical = 0;

            IF @idx IS NOT NULL EXEC sys.sp_executesql @idx;

            SET @tableNo += 1;
            SET @progress = CONCAT(N'copy-progress:', 13 + 39 * @tableNo / @tableCount, N':Creating tables ', @tableNo, N' of ', @tableCount);
            RAISERROR(@progress, 0, 1) WITH NOWAIT;

            FETCH NEXT FROM table_cursor INTO @oldTableId, @newTableId;
        END
        CLOSE table_cursor;
        DEALLOCATE table_cursor;

        INSERT INTO #Timing VALUES ('data_tables', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── Forms ───────────────────────────────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:52:Copying forms and rules', 0, 1) WITH NOWAIT;
        MERGE meta.Form AS t
        USING (SELECT f.Id, f.PublicId, m.NewId AS AppTableId, f.Name, f.IsDefault, f.AutoAddNewFields,
                      f.ShowBuiltInFields, f.SaveOptions, f.DisplayOrder, f.PageNavMode, f.AlwaysTabsOnView, f.ThemeJson,
                      f.IsQuickPeekForm, f.IsQuickPeekDefault
               FROM meta.Form f
               JOIN #IdMap m ON m.Entity = 'AppTable' AND m.OldId = f.AppTableId
               WHERE f.IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppTableId, Name, IsDefault, AutoAddNewFields, ShowBuiltInFields, SaveOptions, DisplayOrder,
                    CreatedOn, CreatedBy, PageNavMode, AlwaysTabsOnView, ThemeJson, IsQuickPeekForm, IsQuickPeekDefault)
            VALUES (s.AppTableId, s.Name, s.IsDefault, s.AutoAddNewFields, s.ShowBuiltInFields, s.SaveOptions,
                    s.DisplayOrder, @now, @UserId, s.PageNavMode, s.AlwaysTabsOnView, s.ThemeJson, s.IsQuickPeekForm,
                    s.IsQuickPeekDefault)
        OUTPUT 'Form', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        MERGE meta.FormPage AS t
        USING (SELECT p.Id, p.PublicId, m.NewId AS FormId, p.Heading, p.DisplayOrder
               FROM meta.FormPage p
               JOIN #IdMap m ON m.Entity = 'Form' AND m.OldId = p.FormId) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (FormId, Heading, DisplayOrder) VALUES (s.FormId, s.Heading, s.DisplayOrder)
        OUTPUT 'FormPage', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        MERGE meta.FormSection AS t
        USING (SELECT x.Id, x.PublicId, m.NewId AS FormId, x.Name, x.ColumnCount, x.ColumnWidths, x.IsCollapsed,
                      x.DisplayOrder, x.GridCols, mp.NewId AS FormPageId, x.IsPinned, x.BackgroundColor,
                      x.BackgroundType, x.BackgroundImage, x.BorderColor, x.BorderWidth, x.ShowDividers,
                      x.DividerColor, x.DividerWidthPx, x.ShowHeading
               FROM meta.FormSection x
               JOIN #IdMap m ON m.Entity = 'Form' AND m.OldId = x.FormId
               LEFT JOIN #IdMap mp ON mp.Entity = 'FormPage' AND mp.OldId = x.FormPageId) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (FormId, Name, ColumnCount, ColumnWidths, IsCollapsed, DisplayOrder, GridCols, FormPageId, IsPinned,
                    BackgroundColor, BackgroundType, BackgroundImage, BorderColor, BorderWidth, ShowDividers,
                    DividerColor, DividerWidthPx, ShowHeading)
            VALUES (s.FormId, s.Name, s.ColumnCount, s.ColumnWidths, s.IsCollapsed, s.DisplayOrder, s.GridCols,
                    s.FormPageId, s.IsPinned, s.BackgroundColor, s.BackgroundType, s.BackgroundImage, s.BorderColor,
                    s.BorderWidth, s.ShowDividers, s.DividerColor, s.DividerWidthPx, s.ShowHeading)
        OUTPUT 'FormSection', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        MERGE meta.FormSectionBlock AS t
        USING (SELECT b.Id, b.PublicId, m.NewId AS FormSectionId, b.Heading, b.BackgroundColor, b.Width,
                      b.DisplayOrder, b.ColStart, b.ColSpan, b.BackgroundType, b.BackgroundImage, b.DividerMode,
                      b.DividerColor, b.DividerWidthPx, b.Name, b.ShowHeading
               FROM meta.FormSectionBlock b
               JOIN #IdMap m ON m.Entity = 'FormSection' AND m.OldId = b.FormSectionId) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (FormSectionId, Heading, BackgroundColor, Width, DisplayOrder, ColStart, ColSpan, BackgroundType,
                    BackgroundImage, DividerMode, DividerColor, DividerWidthPx, Name, ShowHeading)
            VALUES (s.FormSectionId, s.Heading, s.BackgroundColor, s.Width, s.DisplayOrder, s.ColStart, s.ColSpan,
                    s.BackgroundType, s.BackgroundImage, s.DividerMode, s.DividerColor, s.DividerWidthPx, s.Name,
                    s.ShowHeading)
        OUTPUT 'FormSectionBlock', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        -- AppFieldId and RecordPickerFieldId are Fids: unchanged. RecordPickerReportPublicId is mapped after the reports.
        MERGE meta.FormElement AS t
        USING (SELECT e.Id, e.PublicId, ms.NewId AS FormSectionId, mb.NewId AS FormSectionBlockId, e.AppFieldId,
                      e.ElementType, e.ElementContent, e.LabelMode, e.CustomLabel, e.ShowOnAdd, e.ShowOnEdit,
                      e.ShowOnView, e.WidthMode, e.WidthValue, e.HelpTextOverride, e.IsReadOnly, e.IsRequired,
                      e.DisplayAs, e.DisplayOrder, e.ColStart, e.RowStart, e.ColSpan, e.RowSpan, e.GroupId,
                      e.CloneGroupId, mp.NewId AS FormPageId, e.TextStyle, e.BackgroundColor, e.BorderColor,
                      e.BorderWidth, e.ContentWidthMode, e.ContentWidthValue, e.ContentWidthUnit, e.Name,
                      e.RecordPickerMode, e.RecordPickerReportPublicId, e.RecordPickerFieldId
               FROM meta.FormElement e
               JOIN #IdMap ms ON ms.Entity = 'FormSection' AND ms.OldId = e.FormSectionId
               LEFT JOIN #IdMap mb ON mb.Entity = 'FormSectionBlock' AND mb.OldId = e.FormSectionBlockId
               LEFT JOIN #IdMap mp ON mp.Entity = 'FormPage' AND mp.OldId = e.FormPageId) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (FormSectionId, FormSectionBlockId, AppFieldId, ElementType, ElementContent, LabelMode, CustomLabel,
                    ShowOnAdd, ShowOnEdit, ShowOnView, WidthMode, WidthValue, HelpTextOverride, IsReadOnly, IsRequired,
                    DisplayAs, DisplayOrder, ColStart, RowStart, ColSpan, RowSpan, GroupId, CloneGroupId, FormPageId,
                    TextStyle, BackgroundColor, BorderColor, BorderWidth, ContentWidthMode, ContentWidthValue,
                    ContentWidthUnit, Name, RecordPickerMode, RecordPickerReportPublicId, RecordPickerFieldId)
            VALUES (s.FormSectionId, s.FormSectionBlockId, s.AppFieldId, s.ElementType, s.ElementContent, s.LabelMode,
                    s.CustomLabel, s.ShowOnAdd, s.ShowOnEdit, s.ShowOnView, s.WidthMode, s.WidthValue,
                    s.HelpTextOverride, s.IsReadOnly, s.IsRequired, s.DisplayAs, s.DisplayOrder, s.ColStart,
                    s.RowStart, s.ColSpan, s.RowSpan, s.GroupId, s.CloneGroupId, s.FormPageId, s.TextStyle,
                    s.BackgroundColor, s.BorderColor, s.BorderWidth, s.ContentWidthMode, s.ContentWidthValue,
                    s.ContentWidthUnit, s.Name, s.RecordPickerMode, s.RecordPickerReportPublicId, s.RecordPickerFieldId)
        OUTPUT 'FormElement', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        MERGE meta.FormRule AS t
        USING (SELECT r.Id, r.PublicId, m.NewId AS FormId, r.Name, r.Description, r.Tags, r.IsActive,
                      r.IsExpressionMode, r.ExpressionText, r.RunTrigger, r.ConditionLogic, r.DisplayOrder
               FROM meta.FormRule r
               JOIN #IdMap m ON m.Entity = 'Form' AND m.OldId = r.FormId
               WHERE r.IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (FormId, Name, Description, Tags, IsActive, IsExpressionMode, ExpressionText, RunTrigger,
                    ConditionLogic, DisplayOrder, CreatedOn, CreatedBy)
            VALUES (s.FormId, s.Name, s.Description, s.Tags, s.IsActive, s.IsExpressionMode, s.ExpressionText,
                    s.RunTrigger, s.ConditionLogic, s.DisplayOrder, @now, @UserId)
        OUTPUT 'FormRule', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        -- Condition field columns are Fids: unchanged.
        INSERT INTO meta.FormRuleCondition (FormRuleId, AppFieldId, Operator, Value, ValueType, ValueFieldId, DisplayOrder,
                                            ConditionKind, ChangeFromOperator, ChangeFromValue, ChangeFromValueType,
                                            ChangeFromValueFieldId, ChangeToOperator, ChangeToValue, ChangeToValueType,
                                            ChangeToValueFieldId)
        SELECT m.NewId, c.AppFieldId, c.Operator, c.Value, c.ValueType, c.ValueFieldId, c.DisplayOrder, c.ConditionKind,
               c.ChangeFromOperator, c.ChangeFromValue, c.ChangeFromValueType, c.ChangeFromValueFieldId,
               c.ChangeToOperator, c.ChangeToValue, c.ChangeToValueType, c.ChangeToValueFieldId
        FROM meta.FormRuleCondition c
        JOIN #IdMap m ON m.Entity = 'FormRule' AND m.OldId = c.FormRuleId;

        INSERT INTO meta.FormRuleAction (FormRuleId, ActionType, TargetType, TargetElementId, TargetSectionId, TargetBlockId,
                                         ActionValue, DisplayOrder, RunOnceOnActivation, IsExpressionValue,
                                         TargetElementPublicId, TargetSectionPublicId, TargetBlockPublicId,
                                         TargetPagePublicId, ValueSource, SourceFieldId, MessageType, MessageSeverity)
        SELECT m.NewId, a.ActionType, a.TargetType, me.NewId, ms.NewId, mb.NewId, a.ActionValue, a.DisplayOrder,
               a.RunOnceOnActivation, a.IsExpressionValue,
               ISNULL(pe.NewPublicId, a.TargetElementPublicId), ISNULL(ps.NewPublicId, a.TargetSectionPublicId),
               ISNULL(pb.NewPublicId, a.TargetBlockPublicId), ISNULL(pp.NewPublicId, a.TargetPagePublicId),
               a.ValueSource, a.SourceFieldId, a.MessageType, a.MessageSeverity
        FROM meta.FormRuleAction a
        JOIN #IdMap m ON m.Entity = 'FormRule' AND m.OldId = a.FormRuleId
        LEFT JOIN #IdMap me ON me.Entity = 'FormElement'      AND me.OldId = a.TargetElementId
        LEFT JOIN #IdMap ms ON ms.Entity = 'FormSection'      AND ms.OldId = a.TargetSectionId
        LEFT JOIN #IdMap mb ON mb.Entity = 'FormSectionBlock' AND mb.OldId = a.TargetBlockId
        LEFT JOIN #IdMap pe ON pe.OldPublicId = a.TargetElementPublicId
        LEFT JOIN #IdMap ps ON ps.OldPublicId = a.TargetSectionPublicId
        LEFT JOIN #IdMap pb ON pb.OldPublicId = a.TargetBlockPublicId
        LEFT JOIN #IdMap pp ON pp.OldPublicId = a.TargetPagePublicId;

        INSERT INTO #Timing VALUES ('forms_rules', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── Reports ─────────────────────────────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:78:Copying reports', 0, 1) WITH NOWAIT;
        -- Other users' personal reports come along only with their users (Q4).
        MERGE meta.Report AS t
        USING (SELECT r.Id, r.PublicId, m.NewId AS AppTableId, r.OwnerId, r.Name, r.Description, r.ReportType,
                      r.Visibility, r.Definition, r.IsDefault, r.DisplayOrder, mf.NewId AS ViewEditFormId,
                      r.IsDefaultSettingsRecord, mr.NewId AS RelationshipId
               FROM meta.Report r
               JOIN #IdMap m ON m.Entity = 'AppTable' AND m.OldId = r.AppTableId
               LEFT JOIN #IdMap mf ON mf.Entity = 'Form' AND mf.OldId = r.ViewEditFormId
               LEFT JOIN #IdMap mr ON mr.Entity = 'Relationship' AND mr.OldId = r.RelationshipId
               WHERE r.IsDeleted = 0
                 AND (r.Visibility <> 'Personal' OR r.OwnerId = @UserId OR @CopyUsers = 1)) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppTableId, OwnerId, Name, Description, ReportType, Visibility, Definition, IsDefault, DisplayOrder,
                    ViewEditFormId, CreatedOn, CreatedBy, IsDefaultSettingsRecord, RelationshipId)
            VALUES (s.AppTableId, s.OwnerId, s.Name, s.Description, s.ReportType, s.Visibility, s.Definition, s.IsDefault,
                    s.DisplayOrder, s.ViewEditFormId, @now, @UserId, s.IsDefaultSettingsRecord, s.RelationshipId)
        OUTPUT 'Report', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        INSERT INTO meta.ReportGridEditForm (ReportId, FormId, DisplayOrder)
        SELECT mr.NewId, mf.NewId, g.DisplayOrder
        FROM meta.ReportGridEditForm g
        JOIN #IdMap mr ON mr.Entity = 'Report' AND mr.OldId = g.ReportId
        JOIN #IdMap mf ON mf.Entity = 'Form' AND mf.OldId = g.FormId;

        INSERT INTO meta.ReportGridEditRule (ReportId, FormRuleId, DisplayOrder, IsExcluded)
        SELECT mr.NewId, mf.NewId, g.DisplayOrder, g.IsExcluded
        FROM meta.ReportGridEditRule g
        JOIN #IdMap mr ON mr.Entity = 'Report' AND mr.OldId = g.ReportId
        JOIN #IdMap mf ON mf.Entity = 'FormRule' AND mf.OldId = g.FormRuleId;

        UPDATE e SET RecordPickerReportPublicId = m.NewPublicId
        FROM meta.FormElement e
        JOIN #IdMap me ON me.Entity = 'FormElement' AND me.NewId = e.Id
        JOIN #IdMap m ON m.Entity = 'Report' AND m.OldPublicId = e.RecordPickerReportPublicId;

        INSERT INTO #Timing VALUES ('reports', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── Roles and permissions ───────────────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:80:Copying roles, permissions and pages', 0, 1) WITH NOWAIT;
        MERGE meta.AppRole AS t
        USING (SELECT r.Id, r.PublicId, r.Name, r.IsDefault, r.IsSystem, r.ManageableRolesType, r.Rank
               FROM meta.AppRole r
               WHERE r.AppId = @SourceAppId AND r.IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppId, Name, IsDefault, IsSystem, CreatedOn, CreatedBy, ManageableRolesType, Rank)
            VALUES (@NewAppId, s.Name, s.IsDefault, s.IsSystem, @now, @UserId, s.ManageableRolesType, s.Rank)
        OUTPUT 'AppRole', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        UPDATE a SET DefaultAppRoleId = m.NewId
        FROM meta.App a
        JOIN meta.App s ON s.Id = @SourceAppId
        JOIN #IdMap m ON m.Entity = 'AppRole' AND m.OldId = s.DefaultAppRoleId
        WHERE a.Id = @NewAppId;

        INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
        SELECT m.NewId, p.PermissionId
        FROM meta.AppRolePermission p
        JOIN #IdMap m ON m.Entity = 'AppRole' AND m.OldId = p.AppRoleId;

        INSERT INTO meta.AppRoleTablePermission (AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
                                                 CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel)
        SELECT mr.NewId, mt.NewId, p.ViewScope, p.ModifyScope, p.CanAdd, p.CanDelete, p.CanSaveSharedReports,
               p.CanEditFieldProperties, p.FieldAccessLevel
        FROM meta.AppRoleTablePermission p
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = p.AppRoleId
        JOIN #IdMap mt ON mt.Entity = 'AppTable' AND mt.OldId = p.AppTableId;

        INSERT INTO meta.AppRoleFieldPermission (AppRoleId, AppFieldId, Access)
        SELECT mr.NewId, mf.NewId, p.Access
        FROM meta.AppRoleFieldPermission p
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = p.AppRoleId
        JOIN #IdMap mf ON mf.Entity = 'AppField' AND mf.OldId = p.AppFieldId;

        -- FilterJson holds FieldPublicIds; they are rewritten in the JSON stage.
        INSERT INTO meta.AppRoleRecordFilter (AppRoleId, AppTableId, Conjunction, FilterJson, Kind)
        SELECT mr.NewId, mt.NewId, f.Conjunction, f.FilterJson, f.Kind
        FROM meta.AppRoleRecordFilter f
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = f.AppRoleId
        JOIN #IdMap mt ON mt.Entity = 'AppTable' AND mt.OldId = f.AppTableId;

        INSERT INTO meta.AppRoleReport (ReportId, AppRoleId)
        SELECT mp.NewId, mr.NewId
        FROM meta.AppRoleReport p
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = p.AppRoleId
        JOIN #IdMap mp ON mp.Entity = 'Report' AND mp.OldId = p.ReportId;

        INSERT INTO meta.AppRoleTableFormOverride (AppTableId, AppRoleId, EditFormId, AddFormId, CreatedOn, CreatedBy)
        SELECT mt.NewId, mr.NewId, me.NewId, ma.NewId, @now, @UserId
        FROM meta.AppRoleTableFormOverride o
        JOIN #IdMap mt ON mt.Entity = 'AppTable' AND mt.OldId = o.AppTableId
        LEFT JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = o.AppRoleId
        LEFT JOIN #IdMap me ON me.Entity = 'Form' AND me.OldId = o.EditFormId
        LEFT JOIN #IdMap ma ON ma.Entity = 'Form' AND ma.OldId = o.AddFormId
        WHERE o.AppRoleId IS NULL OR mr.NewId IS NOT NULL;

        INSERT INTO meta.AppRoleManageableRole (AppRoleId, ManageableRoleId)
        SELECT mr.NewId, mm.NewId
        FROM meta.AppRoleManageableRole p
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = p.AppRoleId
        JOIN #IdMap mm ON mm.Entity = 'AppRole' AND mm.OldId = p.ManageableRoleId;

        -- ── Pages and dashboards (Q3) ───────────────────────────────────────────────────────────────────────
        -- Current content only, as DuplicatePage does: no version history, version 1.
        MERGE meta.Page AS t
        USING (SELECT p.Id, p.PublicId, p.PageNumber, p.PageType, p.Name, p.Description, p.OwnerId, p.Visibility,
                      p.Definition, p.ContentType, p.CodeHtml, p.CodeCss, p.CodeJs, p.IsPublished, p.ShowInNav,
                      p.NavOrder, p.NavIcon, p.IsDefaultHome
               FROM meta.Page p
               WHERE p.AppId = @SourceAppId AND p.IsDeleted = 0
                 AND (p.Visibility <> 'Personal' OR p.OwnerId = @UserId OR @CopyUsers = 1)) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppId, PageNumber, PageType, Name, Description, OwnerId, Visibility, Definition, ContentType,
                    CodeHtml, CodeCss, CodeJs, IsPublished, CurrentVersionNo, PublishedVersionNo, ShowInNav, NavOrder,
                    NavIcon, IsDefaultHome, CreatedOn, CreatedBy)
            VALUES (@NewAppId, s.PageNumber, s.PageType, s.Name, s.Description, s.OwnerId, s.Visibility, s.Definition,
                    s.ContentType, s.CodeHtml, s.CodeCss, s.CodeJs, s.IsPublished, 1, IIF(s.IsPublished = 1, 1, NULL),
                    s.ShowInNav, s.NavOrder, s.NavIcon, s.IsDefaultHome, @now, @UserId)
        OUTPUT 'Page', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        UPDATE r SET HomePageId = mp.NewId
        FROM meta.AppRole r
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.NewId = r.Id
        JOIN meta.AppRole s ON s.Id = mr.OldId
        JOIN #IdMap mp ON mp.Entity = 'Page' AND mp.OldId = s.HomePageId;

        INSERT INTO meta.AppRolePage (PageId, AppRoleId)
        SELECT mp.NewId, mr.NewId
        FROM meta.AppRolePage p
        JOIN #IdMap mr ON mr.Entity = 'AppRole' AND mr.OldId = p.AppRoleId
        JOIN #IdMap mp ON mp.Entity = 'Page' AND mp.OldId = p.PageId;

        INSERT INTO #Timing VALUES ('roles_pages', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── Saved imports (Q6: schedule off). PowerFlows are not copied (see the top of this file). ─────────────
        RAISERROR(N'copy-progress:82:Copying saved imports and users', 0, 1) WITH NOWAIT;
        -- A source table in another app stays as it is; RunAsUserId becomes the copier, who is Administrator of the copy.
        -- The schedule keeps its settings but is switched off in ScheduleJson as well as by NextRunOn = NULL: with only
        -- NextRunOn cleared, the imports list would show the schedule on while it never runs, and switching it on would do nothing.
        MERGE meta.ImportDefinition AS t
        USING (SELECT d.Id, d.PublicId, md.NewId AS DestinationTableId, d.SourceKind,
                      ISNULL(ms.NewId, d.SourceTableId) AS SourceTableId, d.Name, d.ImportType, d.MergeKeyFid,
                      d.ConditionsJson, d.FieldMappingJson, d.ColumnRulesJson, d.OptionsJson,
                      CASE WHEN d.ScheduleJson IS NULL OR ISJSON(d.ScheduleJson) = 0 THEN d.ScheduleJson
                           WHEN JSON_VALUE(d.ScheduleJson, '$.Enabled') IS NOT NULL THEN JSON_MODIFY(d.ScheduleJson, '$.Enabled', CAST(0 AS BIT))
                           ELSE JSON_MODIFY(d.ScheduleJson, '$.enabled', CAST(0 AS BIT)) END AS ScheduleJson
               FROM meta.ImportDefinition d
               JOIN #IdMap md ON md.Entity = 'AppTable' AND md.OldId = d.DestinationTableId
               LEFT JOIN #IdMap ms ON ms.Entity = 'AppTable' AND ms.OldId = d.SourceTableId
               WHERE d.AppId = @SourceAppId AND d.IsDeleted = 0) AS s
        ON 1 = 0
        WHEN NOT MATCHED THEN
            INSERT (AppId, DestinationTableId, SourceKind, SourceTableId, Name, ImportType, MergeKeyFid, ConditionsJson,
                    FieldMappingJson, ColumnRulesJson, OptionsJson, ScheduleJson, NextRunOn, RunAsUserId,
                    NeedsAttention, CreatedOn, CreatedBy)
            VALUES (@NewAppId, s.DestinationTableId, s.SourceKind, s.SourceTableId, s.Name, s.ImportType, s.MergeKeyFid,
                    s.ConditionsJson, s.FieldMappingJson, s.ColumnRulesJson, s.OptionsJson, s.ScheduleJson, NULL,
                    @UserId, 0, @now, @UserId)
        OUTPUT 'ImportDefinition', s.Id, inserted.Id, s.PublicId, inserted.PublicId
            INTO #IdMap (Entity, OldId, NewId, OldPublicId, NewPublicId);

        -- ── Users (Q1) ──────────────────────────────────────────────────────────────────────────────────────
        DECLARE @adminRoleId BIGINT =
            (SELECT TOP (1) m.NewId
             FROM meta.AppRole r
             JOIN #IdMap m ON m.Entity = 'AppRole' AND m.OldId = r.Id
             WHERE r.AppId = @SourceAppId AND r.IsSystem = 1 AND r.Name = N'Administrator' AND r.IsDeleted = 0
             ORDER BY r.Id);
        IF @adminRoleId IS NULL
            THROW 50500, N'The app to copy has no Administrator role.', 1;

        IF @CopyUsers = 1
        BEGIN
            -- Nobody is emailed. Everyone keeps their roles, the copier included (Quickbase: "keep their existing roles in
            -- the new app"); a user may hold several roles. The copier's own Administrator row is added below, so a
            -- non-admin copier ends up with their original role plus Administrator.
            INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, Status, AddedBy,
                                      CreatedOn, ShowInUserPickers, IsFromGroup, GroupId)
            SELECT @NewAppId, u.UserId, u.UserPublicId, u.UserName, u.UserEmail, m.NewId, u.Status, @UserId, @now,
                   u.ShowInUserPickers, u.IsFromGroup, u.GroupId
            FROM meta.AppUser u
            JOIN #IdMap m ON m.Entity = 'AppRole' AND m.OldId = u.AppRoleId
            WHERE u.AppId = @SourceAppId AND u.IsDeleted = 0
              AND NOT (u.UserId = @UserId AND u.IsFromGroup = 0 AND m.NewId = @adminRoleId);

            INSERT INTO meta.GroupApp (GroupId, AppId, AppRoleId, CreatedOn, CreatedBy)
            SELECT g.GroupId, @NewAppId, m.NewId, @now, @UserId
            FROM meta.GroupApp g
            JOIN #IdMap m ON m.Entity = 'AppRole' AND m.OldId = g.AppRoleId
            WHERE g.AppId = @SourceAppId AND g.IsDeleted = 0;
        END

        -- The copier always manages the copy (as in Quickbase).
        INSERT INTO meta.AppUser (AppId, UserId, UserPublicId, UserName, UserEmail, AppRoleId, AddedBy, CreatedOn, IsFromGroup)
        SELECT @NewAppId, @UserId,
               ISNULL(@UserPublicId, src.UserPublicId), ISNULL(@UserName, src.UserName), ISNULL(@UserEmail, src.UserEmail),
               @adminRoleId, @UserId, @now, 0
        FROM (SELECT 1 AS One) one
        OUTER APPLY (SELECT TOP (1) u.UserPublicId, u.UserName, u.UserEmail
                     FROM meta.AppUser u
                     WHERE u.UserId = @UserId AND u.IsDeleted = 0
                     ORDER BY IIF(u.AppId = @SourceAppId, 0, 1), u.Id DESC) src;

        INSERT INTO #Timing VALUES ('imports_users', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── JSON: point ids at the copy ─────────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:83:Linking settings to the copy', 0, 1) WITH NOWAIT;
        -- 1) PublicIds. Every GUID of a copied row is replaced by the new row's GUID, wherever it sits in the text: a
        --    whole JSON value, a raw GUID column, or inside a longer string such as the "Add child" URL template
        --    (.../tables/<guid>/records/new). GUIDs cannot match by accident, and GUIDs of other apps are not in
        --    #IdMap, so a pointer to another app keeps pointing at the original (Quickbase's shared-value rule).
        DECLARE @jsonTarget TABLE (Tbl VARCHAR(60) NOT NULL, TableName SYSNAME NOT NULL, Col SYSNAME NOT NULL, Scope NVARCHAR(400) NOT NULL);
        INSERT INTO @jsonTarget VALUES
            ('AppField.Settings',               N'AppField',            N'Settings',              N'x.AppTableId IN (SELECT NewId FROM #IdMap WHERE Entity = ''AppTable'')'),
            ('AppTable.DefaultReportSettings',  N'AppTable',            N'DefaultReportSettings', N'x.AppId = @app'),
            ('Report.Definition',               N'Report',              N'Definition',            N'x.AppTableId IN (SELECT NewId FROM #IdMap WHERE Entity = ''AppTable'')'),
            ('Page.Definition',                 N'Page',                N'Definition',            N'x.AppId = @app'),
            ('FormElement.ElementContent',      N'FormElement',         N'ElementContent',        N'x.FormSectionId IN (SELECT NewId FROM #IdMap WHERE Entity = ''FormSection'')'),
            ('FormRuleAction.ActionValue',      N'FormRuleAction',      N'ActionValue',           N'x.FormRuleId IN (SELECT NewId FROM #IdMap WHERE Entity = ''FormRule'')'),
            ('AppRoleRecordFilter.FilterJson',  N'AppRoleRecordFilter', N'FilterJson',            N'x.AppRoleId IN (SELECT NewId FROM #IdMap WHERE Entity = ''AppRole'')'),
            ('ImportDefinition.ConditionsJson', N'ImportDefinition',    N'ConditionsJson',        N'x.AppId = @app'),
            ('ImportDefinition.FieldMappingJson', N'ImportDefinition',  N'FieldMappingJson',      N'x.AppId = @app'),
            ('ImportDefinition.ColumnRulesJson', N'ImportDefinition',   N'ColumnRulesJson',       N'x.AppId = @app'),
            ('ImportDefinition.OptionsJson',    N'ImportDefinition',    N'OptionsJson',           N'x.AppId = @app');

        DECLARE @tbl VARCHAR(60), @tableName SYSNAME, @col SYSNAME, @scope NVARCHAR(400), @sql NVARCHAR(MAX);

        DECLARE json_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Tbl, TableName, Col, Scope FROM @jsonTarget;
        OPEN json_cursor;
        FETCH NEXT FROM json_cursor INTO @tbl, @tableName, @col, @scope;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @sql = N'INSERT INTO #Json (Tbl, RowId, Txt) SELECT @tbl, x.Id, x.' + QUOTENAME(@col)
                     + N' FROM meta.' + QUOTENAME(@tableName) + N' x WHERE ' + @scope
                     + N' AND x.' + QUOTENAME(@col) + N' LIKE N''%________-____-____-____-____________%'';';
            EXEC sys.sp_executesql @sql, N'@tbl VARCHAR(60), @app BIGINT', @tbl = @tbl, @app = @NewAppId;
            FETCH NEXT FROM json_cursor INTO @tbl, @tableName, @col, @scope;
        END
        CLOSE json_cursor;

        DECLARE @hex NVARCHAR(20) = N'[0-9a-fA-F]';
        DECLARE @guidPattern NVARCHAR(500) = N'%' + REPLICATE(@hex, 8) + N'-' + REPLICATE(@hex, 4) + N'-' + REPLICATE(@hex, 4)
                                           + N'-' + REPLICATE(@hex, 4) + N'-' + REPLICATE(@hex, 12) + N'%';
        CREATE TABLE #GuidHit (Tbl VARCHAR(60) NOT NULL, RowId BIGINT NOT NULL, OldText NVARCHAR(36) NOT NULL, NewText NVARCHAR(36) NOT NULL);

        -- Every GUID-shaped piece of each text, found one after the other; only those of copied rows are kept.
        WITH occ AS (
            SELECT j.Tbl, j.RowId, CAST(PATINDEX(@guidPattern, j.Txt) AS BIGINT) AS Pos, j.Txt
            FROM #Json j
            WHERE PATINDEX(@guidPattern, j.Txt) > 0
            UNION ALL
            SELECT o.Tbl, o.RowId, o.Pos + 35 + PATINDEX(@guidPattern, SUBSTRING(o.Txt, o.Pos + 36, 2147483647)), o.Txt
            FROM occ o
            WHERE PATINDEX(@guidPattern, SUBSTRING(o.Txt, o.Pos + 36, 2147483647)) > 0
        )
        INSERT INTO #GuidHit (Tbl, RowId, OldText, NewText)
        SELECT DISTINCT o.Tbl, o.RowId, h.OldText,
               -- keep the casing the text used (JSON writes lower case)
               IIF(h.OldText COLLATE Latin1_General_BIN2 = LOWER(h.OldText) COLLATE Latin1_General_BIN2,
                   LOWER(CONVERT(NVARCHAR(36), m.NewPublicId)), CONVERT(NVARCHAR(36), m.NewPublicId))
        FROM occ o
        CROSS APPLY (SELECT CAST(SUBSTRING(o.Txt, o.Pos, 36) AS NVARCHAR(36)) AS OldText) h
        JOIN #IdMap m ON m.OldPublicId = TRY_CONVERT(UNIQUEIDENTIFIER, h.OldText)
        OPTION (MAXRECURSION 0);

        -- One GUID per text per pass (REPLACE changes all its occurrences); a text rarely holds more than a few.
        UPDATE #Json SET NewTxt = Txt;
        WHILE EXISTS (SELECT 1 FROM #GuidHit)
        BEGIN
            WITH firstHit AS (
                SELECT Tbl, RowId, OldText, NewText,
                       ROW_NUMBER() OVER (PARTITION BY Tbl, RowId ORDER BY OldText) AS rn
                FROM #GuidHit
            )
            UPDATE j SET NewTxt = REPLACE(j.NewTxt, h.OldText, h.NewText), Changed = 1
            FROM #Json j
            JOIN firstHit h ON h.Tbl = j.Tbl AND h.RowId = j.RowId AND h.rn = 1;

            WITH firstHit AS (
                SELECT ROW_NUMBER() OVER (PARTITION BY Tbl, RowId ORDER BY OldText) AS rn
                FROM #GuidHit
            )
            DELETE FROM firstHit WHERE rn = 1;
        END

        OPEN json_cursor;
        FETCH NEXT FROM json_cursor INTO @tbl, @tableName, @col, @scope;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @sql = N'UPDATE x SET ' + QUOTENAME(@col) + N' = j.NewTxt FROM meta.' + QUOTENAME(@tableName)
                     + N' x JOIN #Json j ON j.Tbl = @tbl AND j.RowId = x.Id WHERE j.Changed = 1;';
            EXEC sys.sp_executesql @sql, N'@tbl VARCHAR(60)', @tbl = @tbl;
            FETCH NEXT FROM json_cursor INTO @tbl, @tableName, @col, @scope;
        END
        CLOSE json_cursor;
        DEALLOCATE json_cursor;

        -- 2) Numeric ids, only in AppField.Settings (stored camelCase; PascalCase covered for older rows).
        DECLARE @numericPath TABLE (Path NVARCHAR(100) NOT NULL, Entity VARCHAR(30) NOT NULL);
        INSERT INTO @numericPath VALUES
            (N'$.relationshipId', 'Relationship'), (N'$.RelationshipId', 'Relationship'),
            (N'$.parentTableId',  'AppTable'),     (N'$.ParentTableId',  'AppTable'),
            (N'$.sourceTableId',  'AppTable'),     (N'$.SourceTableId',  'AppTable'),
            (N'$.childTableId',   'AppTable'),     (N'$.ChildTableId',   'AppTable');

        DECLARE @path NVARCHAR(100), @entity VARCHAR(30);
        DECLARE path_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Path, Entity FROM @numericPath;
        OPEN path_cursor;
        FETCH NEXT FROM path_cursor INTO @path, @entity;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            UPDATE f SET Settings = JSON_MODIFY(f.Settings, @path, m.NewId)
            FROM meta.AppField f
            JOIN #IdMap mf ON mf.Entity = 'AppField' AND mf.NewId = f.Id
            JOIN #IdMap m ON m.Entity = @entity AND m.OldId = TRY_CAST(JSON_VALUE(f.Settings, @path) AS BIGINT)
            WHERE ISJSON(f.Settings) = 1;
            FETCH NEXT FROM path_cursor INTO @path, @entity;
        END
        CLOSE path_cursor;
        DEALLOCATE path_cursor;

        -- Reference filter conditions: filterConditions[].junctionTableId.
        UPDATE f SET Settings = JSON_MODIFY(f.Settings, '$.filterConditions', JSON_QUERY(x.Arr))
        FROM meta.AppField f
        JOIN #IdMap mf ON mf.Entity = 'AppField' AND mf.NewId = f.Id
        CROSS APPLY (
            SELECT N'[' + ISNULL(STRING_AGG(CAST(
                       IIF(m.NewId IS NULL, c.value, JSON_MODIFY(c.value, '$.junctionTableId', m.NewId)) AS NVARCHAR(MAX)), N',')
                       WITHIN GROUP (ORDER BY CAST(c.[key] AS INT)), N'') + N']' AS Arr
            FROM OPENJSON(f.Settings, '$.filterConditions') c
            LEFT JOIN #IdMap m ON m.Entity = 'AppTable' AND m.OldId = TRY_CAST(JSON_VALUE(c.value, '$.junctionTableId') AS BIGINT)
        ) x
        WHERE ISJSON(f.Settings) = 1 AND JSON_QUERY(f.Settings, '$.filterConditions') IS NOT NULL;

        INSERT INTO #Timing VALUES ('json', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));
        SET @stageStart = SYSUTCDATETIME();

        -- ── Warnings for the result screen ──────────────────────────────────────────────────────────────────
        RAISERROR(N'copy-progress:98:Finishing', 0, 1) WITH NOWAIT;
        INSERT INTO #Warning (Code, Message)
        SELECT 'CONNECTED_TABLE',
               CONCAT(N'Table "', t.Name, N'" was a connected table. In the copy it is a normal table; connect it again if needed.')
        FROM connected.ConnectedTable ct
        JOIN meta.AppTable t ON t.Id = ct.AppTableId
        WHERE ct.AppId = @SourceAppId AND ct.IsDeleted = 0;

        INSERT INTO #Warning (Code, Message)
        SELECT 'CODE_PAGE',
               CONCAT(N'Code page "', p.Name, N'" was copied as it is. Check it for app, table or report ids that point at the original app.')
        FROM meta.Page p
        JOIN #IdMap m ON m.Entity = 'Page' AND m.NewId = p.Id
        WHERE p.PageType = 'Code';

        INSERT INTO #Warning (Code, Message)
        SELECT 'POWERFLOWS_NOT_COPIED',
               CONCAT(N'The original app has ', COUNT(*), N' PowerFlow(s); they are not copied. Build them in the copy if it needs them.')
        FROM meta.Pipeline p
        WHERE p.AppId = @SourceAppId AND p.IsDeleted = 0
        HAVING COUNT(*) > 0;

        -- App tokens are secrets and are never copied; say so only when the original actually uses some.
        INSERT INTO #Warning (Code, Message)
        SELECT 'APP_TOKENS',
               CONCAT(N'The original app has ', COUNT(*), N' app token(s); they are not copied. Create an app token for the copy before using the API.')
        FROM meta.AppToken tk
        WHERE tk.AppId = @SourceAppId AND tk.IsActive = 1 AND tk.IsDeleted = 0
        HAVING COUNT(*) > 0;

        INSERT INTO #Timing VALUES ('warnings', DATEDIFF(MILLISECOND, @stageStart, SYSUTCDATETIME()));

        IF @ownTran = 1 COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @ownTran = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH

    SELECT Code, Message FROM #Warning ORDER BY Code, Message;
    SELECT Stage, Ms FROM #Timing;
END
