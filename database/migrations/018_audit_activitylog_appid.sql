-- Add AppId to audit.ActivityLog for efficient per-app filtering
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('audit.ActivityLog') AND name = 'AppId'
)
BEGIN
    ALTER TABLE audit.ActivityLog
    ADD AppId BIGINT NULL;
END
GO

-- Covering index for the query API (tenant + date range, desc ordered)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityLog_TenantId_OccurredOn' AND object_id = OBJECT_ID('audit.ActivityLog'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ActivityLog_TenantId_OccurredOn
        ON audit.ActivityLog(TenantId, OccurredOn DESC)
        INCLUDE (UserId, Action, EntityType, EntityId, AppId, IpAddress);
END
GO

-- Index for per-app filtering
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityLog_TenantId_AppId' AND object_id = OBJECT_ID('audit.ActivityLog'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_ActivityLog_TenantId_AppId
        ON audit.ActivityLog(TenantId, AppId)
        INCLUDE (OccurredOn, UserId, Action, EntityType, EntityId);
END
GO
