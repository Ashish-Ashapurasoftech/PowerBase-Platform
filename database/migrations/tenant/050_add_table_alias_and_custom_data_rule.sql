-- Adds the two columns behind the Custom Data Rules feature:
--   Alias           — stable formula reference for the table (_DBID_{TABLE_NAME}), generated
--                      once at creation time and never regenerated on rename. Unique per app.
--   CustomDataRule  — optional formula evaluated as a save-time gate on Add/Update for this
--                      table (see PowerBase.Application.Records.CustomDataRuleValidator).
--
-- NOTE: this repo currently has two conflicting migration sequences using 045-048 — re-check
-- the latest numbering on develop before merging so this doesn't collide; 050 was free as of
-- when this was written.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'Alias')
BEGIN
    ALTER TABLE meta.AppTable ADD Alias NVARCHAR(128) NOT NULL CONSTRAINT DF_AppTable_Alias DEFAULT('');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'CustomDataRule')
BEGIN
    ALTER TABLE meta.AppTable ADD CustomDataRule NVARCHAR(MAX) NULL;
END
GO

-- Backfill Alias for any pre-existing rows (new rows get theirs from CreateTableCommandHandler).
-- Slug matches PowerBase.Domain.Constants.TableAliasNaming.Generate: uppercase alphanumerics,
-- runs of everything else collapsed to a single underscore, prefixed with _DBID_. Collisions
-- within the same app are deduped by appending _2, _3, ... in Id order.
DECLARE @Id BIGINT, @AppId BIGINT, @Name NVARCHAR(200), @Slug NVARCHAR(128), @Alias NVARCHAR(128), @Suffix INT;
DECLARE @i INT, @c NCHAR(1), @out NVARCHAR(128);

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT Id, AppId, Name FROM meta.AppTable WHERE Alias = '' ORDER BY AppId, Id;
OPEN cur;
FETCH NEXT FROM cur INTO @Id, @AppId, @Name;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @i = 1;
    SET @out = '';
    WHILE @i <= LEN(@Name)
    BEGIN
        SET @c = SUBSTRING(@Name, @i, 1);
        IF @c LIKE '[A-Za-z0-9]'
            SET @out = @out + UPPER(@c);
        ELSE IF LEN(@out) > 0 AND RIGHT(@out, 1) <> '_'
            SET @out = @out + '_';
        SET @i = @i + 1;
    END
    IF LEN(@out) > 0 AND RIGHT(@out, 1) = '_' SET @out = LEFT(@out, LEN(@out) - 1);
    IF LEN(@out) = 0 SET @out = 'TABLE';

    SET @Slug = '_DBID_' + @out;
    SET @Alias = @Slug;
    SET @Suffix = 2;
    WHILE EXISTS (SELECT 1 FROM meta.AppTable WHERE AppId = @AppId AND Alias = @Alias AND Id <> @Id)
    BEGIN
        SET @Alias = @Slug + '_' + CAST(@Suffix AS NVARCHAR(10));
        SET @Suffix = @Suffix + 1;
    END

    UPDATE meta.AppTable SET Alias = @Alias WHERE Id = @Id;

    FETCH NEXT FROM cur INTO @Id, @AppId, @Name;
END
CLOSE cur;
DEALLOCATE cur;
GO

-- Enforce uniqueness per app going forward. Filtered on IsDeleted so a deleted table's alias
-- can be reused by a later table.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AppTable_AppId_Alias' AND object_id = OBJECT_ID('meta.AppTable'))
BEGIN
    CREATE UNIQUE INDEX UX_AppTable_AppId_Alias ON meta.AppTable(AppId, Alias) WHERE IsDeleted = 0;
END
GO
