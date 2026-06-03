-- Tenant DB baseline: per-tenant activity log (no TenantId — one tenant per DB)

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'ActivityLog')
BEGIN
    CREATE TABLE audit.ActivityLog (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        UserId     BIGINT        NULL,
        UserName   NVARCHAR(256) NULL,
        UserEmail  NVARCHAR(256) NULL,
        Action     NVARCHAR(100) NOT NULL,
        EntityType NVARCHAR(100) NOT NULL,
        EntityId   NVARCHAR(100) NULL,
        AppId      BIGINT        NULL,
        OldValues  NVARCHAR(MAX) NULL,
        NewValues  NVARCHAR(MAX) NULL,
        IpAddress  NVARCHAR(50)  NULL,
        UserAgent  NVARCHAR(500) NULL,
        OccurredOn DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ActivityLog PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_ActivityLog_OccurredOn ON audit.ActivityLog(OccurredOn DESC);
END
GO
