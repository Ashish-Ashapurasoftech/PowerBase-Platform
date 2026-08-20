-- Add Fid (controlled field ID) column to meta.AppField.
-- Fid is a user-assigned stable identifier, distinct from the physical column name.
-- It must be unique within a table when set.
IF OBJECT_ID('meta.AppField', 'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'meta' AND t.name = 'AppField' AND c.name = 'Fid')
    BEGIN
        ALTER TABLE meta.AppField ADD Fid NVARCHAR(64) NULL;
    END

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'meta' AND t.name = 'AppField' AND i.name = 'UX_AppField_TableFid')
    BEGIN
        -- dynamic SQL so it doesn't fail parsing if the table is missing
        EXEC('CREATE UNIQUE NONCLUSTERED INDEX UX_AppField_TableFid
            ON meta.AppField(AppTableId, Fid)
            WHERE IsDeleted = 0 AND Fid IS NOT NULL;');
    END
END
GO
