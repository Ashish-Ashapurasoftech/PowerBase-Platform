IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'UserSession')
BEGIN
    CREATE TABLE audit.UserSession (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        UserId        BIGINT           NOT NULL,
        TenantId      BIGINT           NULL,
        JwtId         UNIQUEIDENTIFIER NOT NULL,
        IpAddress     VARCHAR(45)      NULL,
        UserAgent     NVARCHAR(500)    NULL,
        IssuedOn      DATETIME2(3)     NOT NULL CONSTRAINT df_UserSession_IssuedOn DEFAULT SYSUTCDATETIME(),
        ExpiresOn     DATETIME2(3)     NOT NULL,
        RevokedOn     DATETIME2(3)     NULL,
        RevokedReason VARCHAR(50)      NULL,
        CONSTRAINT pk_UserSession PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'LoginAttempt')
BEGIN
    CREATE TABLE audit.LoginAttempt (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        EmailAttempted NVARCHAR(256) NOT NULL,
        UserId         BIGINT        NULL,
        IpAddress      NVARCHAR(50)  NULL,
        UserAgent      NVARCHAR(500) NULL,
        WasSuccessful  BIT           NOT NULL DEFAULT 0,
        FailureReason  NVARCHAR(200) NULL,
        AttemptedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LoginAttempt PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'PasswordReset')
BEGIN
    CREATE TABLE audit.PasswordReset (
        Id                 BIGINT IDENTITY(1,1) NOT NULL,
        UserId             BIGINT        NOT NULL,
        TokenHash          NVARCHAR(256) NOT NULL,
        IpAddressRequested NVARCHAR(50)  NULL,
        UsedFromIp         NVARCHAR(50)  NULL,
        ExpiresOn          DATETIME2(3)  NOT NULL,
        UsedOn             DATETIME2(3)  NULL,
        RequestedOn        DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PasswordReset PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PasswordReset_User FOREIGN KEY (UserId) REFERENCES core.[User](Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'ActivityLog')
BEGIN
    CREATE TABLE audit.ActivityLog (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT        NOT NULL,
        UserId       BIGINT        NULL,
        Action       NVARCHAR(100) NOT NULL,
        EntityType   NVARCHAR(100) NOT NULL,
        EntityId     NVARCHAR(100) NULL,
        OldValues    NVARCHAR(MAX) NULL,
        NewValues    NVARCHAR(MAX) NULL,
        IpAddress    NVARCHAR(50)  NULL,
        UserAgent    NVARCHAR(500) NULL,
        OccurredOn   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ActivityLog PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_ActivityLog_TenantId ON audit.ActivityLog(TenantId);
END
GO
