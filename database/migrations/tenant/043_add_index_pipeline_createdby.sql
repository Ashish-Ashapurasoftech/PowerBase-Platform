-- Shifting pipeline visibility to User-based ownership
-- 1. Deterministic Backfill of CreatedBy from audit.ActivityLog (where CreatedBy = 0)
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Pipeline') AND name = 'CreatedBy')
BEGIN
    -- Backfill from ActivityLog where EntityType is 'Pipeline' and Action is 'Created'
    UPDATE p
    SET p.CreatedBy = l.UserId
    FROM meta.Pipeline p
    INNER JOIN audit.ActivityLog l ON l.EntityId = CAST(p.PublicId AS VARCHAR(100))
    WHERE p.CreatedBy = 0 
      AND l.EntityType = 'Pipeline' 
      AND l.Action = 'Created' 
      AND l.UserId IS NOT NULL 
      AND l.UserId <> 0;
END
GO

-- 2. Create non-clustered index on CreatedBy for optimized list/count queries
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Pipeline_CreatedBy' AND object_id = OBJECT_ID('meta.Pipeline'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Pipeline_CreatedBy ON meta.Pipeline(CreatedBy) WHERE IsDeleted = 0;
END
GO
