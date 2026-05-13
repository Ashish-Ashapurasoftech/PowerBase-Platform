IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'UserSession')
BEGIN
    CREATE TABLE audit.UserSession (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        UserId      BIGINT        NOT NULL,
        TenantId    BIGINT        NOT NULL,
        JwtId       NVARCHAR(100) NOT NULL,
        IpAddress   NVARCHAR(50)  NULL,
        IsRevoked   BIT           NOT NULL DEFAULT 0,
        ExpiresAt   DATETIME2     NOT NULL,
        CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UserSession PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_UserSession_JwtId UNIQUE (JwtId),
        CONSTRAINT FK_UserSession_User FOREIGN KEY (UserId) REFERENCES core.[User](Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'LoginAttempt')
BEGIN
    CREATE TABLE audit.LoginAttempt (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        Email       NVARCHAR(256) NOT NULL,
        IpAddress   NVARCHAR(50)  NULL,
        IsSuccess   BIT           NOT NULL DEFAULT 0,
        AttemptedAt DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_LoginAttempt PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'PasswordReset')
BEGIN
    CREATE TABLE audit.PasswordReset (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        UserId      BIGINT        NOT NULL,
        TokenHash   NVARCHAR(256) NOT NULL,
        IsUsed      BIT           NOT NULL DEFAULT 0,
        ExpiresAt   DATETIME2     NOT NULL,
        CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PasswordReset PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PasswordReset_User FOREIGN KEY (UserId) REFERENCES core.[User](Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'ActivityLog')
BEGIN
    CREATE TABLE audit.ActivityLog (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        TenantId   BIGINT        NOT NULL,
        UserId     BIGINT        NULL,
        Action     NVARCHAR(100) NOT NULL,
        Resource   NVARCHAR(100) NOT NULL,
        ResourceId NVARCHAR(100) NULL,
        IpAddress  NVARCHAR(50)  NULL,
        CreatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ActivityLog PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_ActivityLog_TenantId ON audit.ActivityLog(TenantId);
END
GO
