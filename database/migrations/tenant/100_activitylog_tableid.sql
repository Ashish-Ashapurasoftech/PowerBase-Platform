-- Migration 100: audit.ActivityLog.TableId — the app table an audit row belongs to (records, fields, tables, forms,
-- form rules, reports), so the Application Audit page can filter by table.
-- Rows written before this migration keep TableId NULL (only "App Table" rows can still be matched, by EntityId).
IF COL_LENGTH('audit.ActivityLog', 'TableId') IS NULL
    ALTER TABLE audit.ActivityLog ADD TableId BIGINT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityLog_App_Table_OccurredOn' AND object_id = OBJECT_ID('audit.ActivityLog'))
    CREATE NONCLUSTERED INDEX IX_ActivityLog_App_Table_OccurredOn
        ON audit.ActivityLog (AppId, TableId, OccurredOn DESC);
GO
