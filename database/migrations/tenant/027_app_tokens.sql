-- Migration 027: App Tokens Table (Tenant Workspace)
IF NOT EXISTS (SELECT * FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'meta' AND t.name = 'AppToken')
BEGIN
    CREATE TABLE meta.AppToken (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId BIGINT NOT NULL,
        AppId BIGINT NOT NULL,
        CreatedByUserId BIGINT NOT NULL DEFAULT 0,
        TokenName NVARCHAR(200) NOT NULL,
        Description NVARCHAR(1000) NULL,
        TokenHash NVARCHAR(128) NOT NULL,
        TokenPrefix NVARCHAR(100) NOT NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUsedAt DATETIME2 NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL,

        CONSTRAINT FK_AppToken_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );

    CREATE UNIQUE INDEX UX_AppToken_PublicId ON meta.AppToken(PublicId);
    CREATE INDEX IX_AppToken_TenantId_AppId ON meta.AppToken(TenantId, AppId) WHERE IsDeleted = 0;
    CREATE INDEX IX_AppToken_TokenHash ON meta.AppToken(TokenHash) WHERE IsDeleted = 0 AND IsActive = 1;
END;
ELSE
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppToken') AND name = 'CreatedByUserId')
    BEGIN
        ALTER TABLE meta.AppToken ADD CreatedByUserId BIGINT NOT NULL DEFAULT 0;
    END;

    IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppToken') AND name = 'TokenPrefix' AND max_length < 200)
    BEGIN
        ALTER TABLE meta.AppToken ALTER COLUMN TokenPrefix NVARCHAR(100) NOT NULL;
    END;
END;
