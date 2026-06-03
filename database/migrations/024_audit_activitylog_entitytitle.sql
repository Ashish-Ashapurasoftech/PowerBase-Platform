-- Migration: Add EntityTitle to audit.ActivityLog
-- NOTE: audit.ActivityLog was moved to per-tenant DBs in migration 031.
-- This root migration is retained only for the migration-tracking record
-- (so it gets marked as applied and never retried).
-- The actual column addition for tenant DBs is in:
--   database/migrations/tenant/007_audit_activitylog_entitytitle.sql

IF OBJECT_ID('audit.ActivityLog', 'U') IS NOT NULL
    AND NOT EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID('audit.ActivityLog') AND name = 'EntityTitle'
    )
BEGIN
    ALTER TABLE audit.ActivityLog ADD EntityTitle NVARCHAR(MAX) NULL;
END
