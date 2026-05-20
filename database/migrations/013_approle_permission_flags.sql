-- Add per-role record permission flags to meta.AppRole (Quickbase compatibility).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'CanViewRecords')
BEGIN
    ALTER TABLE meta.AppRole ADD
        CanViewRecords   BIT NOT NULL DEFAULT 1,
        CanAddRecords    BIT NOT NULL DEFAULT 1,
        CanEditRecords   BIT NOT NULL DEFAULT 1,
        CanDeleteRecords BIT NOT NULL DEFAULT 0;
END
GO

-- Viewer: read-only
UPDATE meta.AppRole
SET CanAddRecords = 0, CanEditRecords = 0, CanDeleteRecords = 0
WHERE Name = 'Viewer' AND IsSystem = 1 AND IsDeleted = 0;
GO

-- Participant: view + add + edit, no delete
UPDATE meta.AppRole
SET CanDeleteRecords = 0
WHERE Name = 'Participant' AND IsSystem = 1 AND IsDeleted = 0;
GO

-- Administrator: full access including delete
UPDATE meta.AppRole
SET CanDeleteRecords = 1
WHERE Name = 'Administrator' AND IsSystem = 1 AND IsDeleted = 0;
GO
