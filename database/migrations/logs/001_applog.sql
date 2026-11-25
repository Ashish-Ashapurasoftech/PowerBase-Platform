-- Logs database (PowerBase_Logs, shared by every tenant) — migration 001: applog.AppLog.
-- The application log behind App Settings > Application Audit: one row per change made in an app, written by the API's
-- AppLogMiddleware for every POST / PUT / PATCH / DELETE request (and for changes made by background work such as
-- PowerFlows). It is separate from each tenant's audit.ActivityLog, which stays the record page Stream's log
-- (auditable fields only): this log keeps everything, so it lives in its own database.
-- Ids are the tenant database's own ids (TenantId + AppId / TableId), never joined across databases.
IF SCHEMA_ID('applog') IS NULL
    EXEC('CREATE SCHEMA applog');
GO
IF OBJECT_ID('applog.AppLog', 'U') IS NULL
BEGIN
    CREATE TABLE applog.AppLog (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT         NULL,
        AppId        BIGINT         NULL,
        TableId      BIGINT         NULL,
        RecordNumber BIGINT         NULL,
        UserId       BIGINT         NULL,
        UserName     NVARCHAR(256)  NULL,
        UserEmail    NVARCHAR(256)  NULL,
        Action       NVARCHAR(100)  NOT NULL,
        EntityType   NVARCHAR(100)  NOT NULL,
        EntityId     NVARCHAR(100)  NULL,
        Description  NVARCHAR(MAX)  NULL,
        OldValues    NVARCHAR(MAX)  NULL,
        NewValues    NVARCHAR(MAX)  NULL,
        HttpMethod   VARCHAR(10)    NULL,
        Path         NVARCHAR(2000) NULL,
        StatusCode   INT            NULL,
        DurationMs   INT            NULL,
        RequestBody  NVARCHAR(MAX)  NULL,
        IpAddress    NVARCHAR(50)   NULL,
        UserAgent    NVARCHAR(500)  NULL,
        OccurredOn   DATETIME2(3)   NOT NULL CONSTRAINT DF_AppLog_OccurredOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_AppLog PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppLog_Tenant_App_OccurredOn ON applog.AppLog (TenantId, AppId, OccurredOn DESC);
    CREATE NONCLUSTERED INDEX IX_AppLog_Tenant_Table_Record ON applog.AppLog (TenantId, TableId, RecordNumber) WHERE TableId IS NOT NULL;
END
GO
