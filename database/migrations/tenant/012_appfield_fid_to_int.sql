-- Repurpose Fid from NVARCHAR(64) (user-settable) to INT (auto-assigned sequential FID).
-- System fields are backfilled with canonical Quickbase-compatible FIDs.
IF EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppField' AND i.name = 'UX_AppField_TableFid')
BEGIN
    DROP INDEX UX_AppField_TableFid ON meta.AppField;
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppField' AND c.name = 'Fid')
BEGIN
    ALTER TABLE meta.AppField DROP COLUMN Fid;
END
GO

ALTER TABLE meta.AppField ADD Fid INT NULL;
GO

CREATE UNIQUE NONCLUSTERED INDEX UX_AppField_TableFid
    ON meta.AppField(AppTableId, Fid)
    WHERE IsDeleted = 0 AND Fid IS NOT NULL;
GO

-- Backfill canonical FIDs for existing system fields by their physical column name.
UPDATE meta.AppField SET Fid = 1 WHERE PhysicalColumnName = 'CreatedOn'  AND IsSystem = 1;
UPDATE meta.AppField SET Fid = 2 WHERE PhysicalColumnName = 'ModifiedOn' AND IsSystem = 1;
UPDATE meta.AppField SET Fid = 3 WHERE PhysicalColumnName = 'Id'         AND IsSystem = 1;
UPDATE meta.AppField SET Fid = 4 WHERE PhysicalColumnName = 'CreatedBy'  AND IsSystem = 1;
UPDATE meta.AppField SET Fid = 5 WHERE PhysicalColumnName = 'ModifiedBy' AND IsSystem = 1;
GO
