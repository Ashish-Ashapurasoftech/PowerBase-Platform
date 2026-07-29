-- Migration 041: User Tokens, App Restrictions, and Permission Seeding

IF NOT EXISTS (SELECT * FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'core' AND t.name = 'UserToken')
BEGIN
    CREATE TABLE core.UserToken (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId BIGINT NOT NULL,
        UserId BIGINT NOT NULL,
        TokenName NVARCHAR(200) NOT NULL,
        Description NVARCHAR(1000) NULL,
        TokenHash NVARCHAR(128) NOT NULL,
        TokenPrefix NVARCHAR(20) NOT NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        AccessAllApps BIT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUsedAt DATETIME2 NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL,

        CONSTRAINT FK_UserToken_User FOREIGN KEY (UserId) REFERENCES core.[User](Id)
    );

    CREATE UNIQUE INDEX UX_UserToken_PublicId ON core.UserToken(PublicId);
    CREATE INDEX IX_UserToken_TenantId_UserId ON core.UserToken(TenantId, UserId) WHERE IsDeleted = 0;
    CREATE INDEX IX_UserToken_TokenHash ON core.UserToken(TokenHash) WHERE IsDeleted = 0 AND IsActive = 1;
END;

IF NOT EXISTS (SELECT * FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'core' AND t.name = 'UserTokenAppRestriction')
BEGIN
    CREATE TABLE core.UserTokenAppRestriction (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        UserTokenId BIGINT NOT NULL,
        AppId BIGINT NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT FK_UserTokenAppRestriction_UserToken FOREIGN KEY (UserTokenId) REFERENCES core.UserToken(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_UserTokenAppRestriction_UserTokenId ON core.UserTokenAppRestriction(UserTokenId);
END;

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'token:create')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('token:create', 'Create User Tokens', 'Create personal user tokens for integrations');
END;
