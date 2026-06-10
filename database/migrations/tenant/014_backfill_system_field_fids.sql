-- Backfill Fid for system fields that were inserted before the FID-aware code was deployed.
-- Safe to re-run: only updates rows where Fid IS NULL and PhysicalColumnName matches.
UPDATE meta.AppField SET Fid = 1 WHERE PhysicalColumnName = 'CreatedOn'  AND IsSystem = 1 AND IsDeleted = 0 AND Fid IS NULL;
UPDATE meta.AppField SET Fid = 2 WHERE PhysicalColumnName = 'ModifiedOn' AND IsSystem = 1 AND IsDeleted = 0 AND Fid IS NULL;
UPDATE meta.AppField SET Fid = 3 WHERE PhysicalColumnName = 'Id'         AND IsSystem = 1 AND IsDeleted = 0 AND Fid IS NULL;
UPDATE meta.AppField SET Fid = 4 WHERE PhysicalColumnName = 'CreatedBy'  AND IsSystem = 1 AND IsDeleted = 0 AND Fid IS NULL;
UPDATE meta.AppField SET Fid = 5 WHERE PhysicalColumnName = 'ModifiedBy' AND IsSystem = 1 AND IsDeleted = 0 AND Fid IS NULL;
GO
