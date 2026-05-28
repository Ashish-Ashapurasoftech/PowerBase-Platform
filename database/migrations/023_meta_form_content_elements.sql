-- Migration 023: Add content element support and per-column width to form layout
-- Content elements (StaticText, Divider, Button) are not backed by an AppField.
-- AppFieldId becomes nullable; NULL FK values are not enforced in SQL Server.

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'meta' AND TABLE_NAME = 'FormElement' AND COLUMN_NAME = 'ElementType'
)
BEGIN
    -- 1. Make AppFieldId nullable (existing FK still enforces on non-NULL values)
    ALTER TABLE meta.FormElement ALTER COLUMN AppFieldId BIGINT NULL;

    -- 2. ElementType discriminator — 'Field' for all existing rows
    ALTER TABLE meta.FormElement ADD ElementType VARCHAR(30) NOT NULL DEFAULT 'Field';

    -- 3. ElementContent — text for StaticText; JSON for Button; NULL for Divider/Field
    ALTER TABLE meta.FormElement ADD ElementContent NVARCHAR(MAX) NULL;
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'meta' AND TABLE_NAME = 'FormSection' AND COLUMN_NAME = 'ColumnWidths'
)
BEGIN
    -- 4. ColumnWidths — comma-separated ratio integers e.g. "1,1" or "3,7"; NULL = equal columns
    ALTER TABLE meta.FormSection ADD ColumnWidths NVARCHAR(100) NULL;
END;
GO
