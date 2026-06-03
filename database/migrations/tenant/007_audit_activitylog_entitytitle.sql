-- Tenant migration: add EntityTitle to audit.ActivityLog
-- audit.ActivityLog lives in tenant DBs only (moved from shared DB in root migration 031).
-- This is idempotent: safe to run on DBs provisioned before this column was added.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('audit.ActivityLog') AND name = 'EntityTitle'
)
BEGIN
    ALTER TABLE audit.ActivityLog ADD EntityTitle NVARCHAR(MAX) NULL;
END
GO
