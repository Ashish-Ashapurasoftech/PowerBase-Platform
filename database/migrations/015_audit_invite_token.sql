IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'InviteToken')
BEGIN
    CREATE TABLE audit.InviteToken (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        UserId       BIGINT        NOT NULL,
        TenantId     BIGINT        NOT NULL,
        TenantRoleId BIGINT        NOT NULL,
        TokenHash    NVARCHAR(256) NOT NULL,
        InvitedBy    BIGINT        NOT NULL,
        ExpiresOn    DATETIME2(3)  NOT NULL,
        UsedOn       DATETIME2(3)  NULL,
        CreatedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_InviteToken PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_InviteToken_TokenHash UNIQUE (TokenHash),
        CONSTRAINT FK_InviteToken_User   FOREIGN KEY (UserId)   REFERENCES core.[User](Id),
        CONSTRAINT FK_InviteToken_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id)
    );
END
GO
