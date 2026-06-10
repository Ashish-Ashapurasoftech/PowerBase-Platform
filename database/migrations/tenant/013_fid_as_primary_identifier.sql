-- Make FID the primary field identifier throughout:
--   1. Rename data columns f_{AppField.Id} → f_{AppField.Fid}
--   2. Update FormElement.AppFieldId to store Fid values
--   3. Update FormRuleCondition field references to store Fid values
--   4. Update Report.Definition JSON column/filter/sort field IDs to Fid values

-- ─── 1. Rename physical data columns ─────────────────────────────────────────
DECLARE @oldCol NVARCHAR(128), @newCol NVARCHAR(128), @tblName NVARCHAR(128)
DECLARE @sql NVARCHAR(MAX)

DECLARE colCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        'f_' + CAST(af.Id AS NVARCHAR(20))      AS OldCol,
        'f_' + CAST(af.Fid AS NVARCHAR(20))     AS NewCol,
        at2.PhysicalTableName                    AS TblName
    FROM meta.AppField af
    JOIN meta.AppTable at2 ON at2.Id = af.AppTableId
    WHERE af.IsSystem = 0
      AND af.IsDeleted = 0
      AND af.Fid IS NOT NULL
      AND at2.PhysicalTableName IS NOT NULL

OPEN colCur
FETCH NEXT FROM colCur INTO @oldCol, @newCol, @tblName
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @oldCol <> @newCol
    BEGIN
        -- Only rename if the old column actually exists
        IF EXISTS (
            SELECT 1 FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'data' AND t.name = @tblName AND c.name = @oldCol)
        BEGIN
            SET @sql = N'EXEC sp_rename ''data.' + @tblName + '.' + @oldCol + ''', ''' + @newCol + ''', ''COLUMN'''
            EXEC sp_executesql @sql
        END
    END
    FETCH NEXT FROM colCur INTO @oldCol, @newCol, @tblName
END
CLOSE colCur
DEALLOCATE colCur
GO

-- ─── 2. Update FormElement.AppFieldId to store Fid ───────────────────────────
-- Drop FK and index first (ALTER COLUMN requires no dependent objects)
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_FormElement_AppField'
      AND parent_object_id = OBJECT_ID('meta.FormElement'))
    ALTER TABLE meta.FormElement DROP CONSTRAINT FK_FormElement_AppField;
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'FormElement' AND i.name = 'IX_FormElement_AppFieldId')
    DROP INDEX IX_FormElement_AppFieldId ON meta.FormElement;
GO

UPDATE fe
SET fe.AppFieldId = CAST(af.Fid AS BIGINT)
FROM meta.FormElement fe
JOIN meta.AppField af ON af.Id = fe.AppFieldId
WHERE af.Fid IS NOT NULL;
GO

ALTER TABLE meta.FormElement ALTER COLUMN AppFieldId INT NOT NULL;
GO

CREATE NONCLUSTERED INDEX IX_FormElement_AppFieldId ON meta.FormElement(AppFieldId);
GO

-- ─── 3. Update FormRuleCondition field references to store Fid ───────────────
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_FormRuleCondition_Field'
      AND parent_object_id = OBJECT_ID('meta.FormRuleCondition'))
    ALTER TABLE meta.FormRuleCondition DROP CONSTRAINT FK_FormRuleCondition_Field;
GO

UPDATE frc
SET frc.AppFieldId = CAST(af.Fid AS INT)
FROM meta.FormRuleCondition frc
JOIN meta.AppField af ON af.Id = frc.AppFieldId
WHERE af.Fid IS NOT NULL;

UPDATE frc
SET frc.ValueFieldId = CAST(af.Fid AS INT)
FROM meta.FormRuleCondition frc
JOIN meta.AppField af ON af.Id = frc.ValueFieldId
WHERE frc.ValueFieldId IS NOT NULL AND af.Fid IS NOT NULL;
GO

ALTER TABLE meta.FormRuleCondition ALTER COLUMN AppFieldId INT NOT NULL;
ALTER TABLE meta.FormRuleCondition ALTER COLUMN ValueFieldId INT NULL;
GO

-- ─── 4. Update Report.Definition JSON field IDs ──────────────────────────────
-- Build a mapping of AppField.Id → Fid scoped by AppTable
-- We replace each old Id with its Fid in the Definition JSON using string replacement.
-- String replacement is safe here because field IDs only appear as integer values
-- inside JSON arrays/properties and are bounded by characters that can't be part of an ID.

DECLARE @reportId BIGINT, @tableId BIGINT, @definition NVARCHAR(MAX)
DECLARE @oldId BIGINT, @fid INT

DECLARE repCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT r.Id, r.AppTableId, r.Definition
    FROM meta.Report r
    WHERE r.IsDeleted = 0 AND r.Definition IS NOT NULL

OPEN repCur
FETCH NEXT FROM repCur INTO @reportId, @tableId, @definition

WHILE @@FETCH_STATUS = 0
BEGIN
    -- For each field in this table, replace occurrences of old Id with Fid in the JSON
    DECLARE fieldCur CURSOR LOCAL FAST_FORWARD FOR
        SELECT af.Id, af.Fid
        FROM meta.AppField af
        WHERE af.AppTableId = @tableId
          AND af.IsDeleted = 0
          AND af.Fid IS NOT NULL
          AND af.Id <> af.Fid  -- only process fields where Id differs from Fid
        ORDER BY af.Id DESC    -- replace larger IDs first to avoid partial replacements

    OPEN fieldCur
    FETCH NEXT FROM fieldCur INTO @oldId, @fid

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Replace patterns where field ID appears as a JSON value:
        -- as array element: ,42, or [42, or ,42] or [42]
        -- as object value:  ":42, or ":42} or ":42\n
        SET @definition = REPLACE(@definition, ',' + CAST(@oldId AS NVARCHAR) + ',',   ',' + CAST(@fid AS NVARCHAR) + ',')
        SET @definition = REPLACE(@definition, '[' + CAST(@oldId AS NVARCHAR) + ',',   '[' + CAST(@fid AS NVARCHAR) + ',')
        SET @definition = REPLACE(@definition, ',' + CAST(@oldId AS NVARCHAR) + ']',   ',' + CAST(@fid AS NVARCHAR) + ']')
        SET @definition = REPLACE(@definition, '[' + CAST(@oldId AS NVARCHAR) + ']',   '[' + CAST(@fid AS NVARCHAR) + ']')
        SET @definition = REPLACE(@definition, '":' + CAST(@oldId AS NVARCHAR) + ',',  '":' + CAST(@fid AS NVARCHAR) + ',')
        SET @definition = REPLACE(@definition, '":' + CAST(@oldId AS NVARCHAR) + '}',  '":' + CAST(@fid AS NVARCHAR) + '}')
        SET @definition = REPLACE(@definition, '":' + CAST(@oldId AS NVARCHAR) + ' ',  '":' + CAST(@fid AS NVARCHAR) + ' ')

        FETCH NEXT FROM fieldCur INTO @oldId, @fid
    END

    CLOSE fieldCur
    DEALLOCATE fieldCur

    UPDATE meta.Report SET Definition = @definition WHERE Id = @reportId

    FETCH NEXT FROM repCur INTO @reportId, @tableId, @definition
END

CLOSE repCur
DEALLOCATE repCur
GO
