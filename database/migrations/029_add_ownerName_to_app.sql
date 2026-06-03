-- Phase 1: Denormalize OwnerName into meta.App to eliminate the cross-DB
-- JOIN to core.[User] in AppRepository.ListByUserSql.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.App') AND name = 'OwnerName')
BEGIN
    ALTER TABLE meta.App ADD OwnerName NVARCHAR(256) NULL;
END
GO

-- Backfill from core.[User] for existing rows.
UPDATE a
SET    a.OwnerName = u.Name
FROM   meta.App  a
JOIN   core.[User] u ON u.Id = a.OwnerId
WHERE  a.OwnerName IS NULL;
GO
