-- Migration: Add EntityTitle to audit.ActivityLog

ALTER TABLE audit.ActivityLog
ADD EntityTitle NVARCHAR(MAX) NULL;
