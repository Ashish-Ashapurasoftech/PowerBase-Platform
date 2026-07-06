-- Tenant DB: make meta.FormElement.AppFieldId nullable again.
--
-- Content elements (StaticText, Divider, Button, Report) are not backed by an AppField, so their
-- AppFieldId is NULL. Migration 013 (fid-as-primary-identifier) re-imposed INT NOT NULL on this
-- column, which blocks saving any non-field element. This restores nullability. There is no FK on
-- AppFieldId after 013 (it stores Fid values, not AppField.Id), so only the index is a dependency.

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.FormElement') AND name = 'AppFieldId' AND is_nullable = 0)
BEGIN
    -- Drop the dependent index before ALTER COLUMN.
    IF EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'meta' AND t.name = 'FormElement' AND i.name = 'IX_FormElement_AppFieldId')
        DROP INDEX IX_FormElement_AppFieldId ON meta.FormElement;

    ALTER TABLE meta.FormElement ALTER COLUMN AppFieldId INT NULL;

    CREATE NONCLUSTERED INDEX IX_FormElement_AppFieldId ON meta.FormElement(AppFieldId);
END
GO
