-- Microsoft (Outlook) accounts connected from PowerFlows "Send an Email".
--
-- One row per (tenant, PowerBase user, Microsoft account). The account belongs to the
-- user who connected it and is listed in every pipeline that user edits, like Quickbase
-- channel accounts. Tokens are stored encrypted (ASP.NET Data Protection); the raw
-- refresh/access tokens never leave the API.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'MicrosoftConnection' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.MicrosoftConnection (
        Id                     BIGINT IDENTITY(1,1) PRIMARY KEY,
        PublicId               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId               BIGINT NOT NULL,
        CreatedByUserId        BIGINT NOT NULL,

        Provider               NVARCHAR(50) NOT NULL DEFAULT 'microsoft-outlook',
        MicrosoftUserId        NVARCHAR(100) NOT NULL,
        AccountEmail           NVARCHAR(320) NOT NULL,
        DisplayName            NVARCHAR(200) NOT NULL,
        Scopes                 NVARCHAR(2000) NOT NULL,

        EncryptedRefreshToken  NVARCHAR(MAX) NOT NULL,
        EncryptedAccessToken   NVARCHAR(MAX) NULL,
        AccessTokenExpiresAt   DATETIMEOFFSET NULL,

        Status                 NVARCHAR(32) NOT NULL DEFAULT 'connected',
        LastError              NVARCHAR(1000) NULL,

        CreatedAt              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAt             DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUsedAt             DATETIME2 NULL,
        IsDeleted              BIT NOT NULL DEFAULT 0,
        DeletedAt              DATETIME2 NULL,
        RowVersion             ROWVERSION NOT NULL,

        CONSTRAINT CK_MicrosoftConnection_Status CHECK (Status IN ('connected', 'reconnect_required'))
    );

    CREATE UNIQUE INDEX UX_MicrosoftConnection_PublicId ON meta.MicrosoftConnection (PublicId);

    -- Reconnecting the same Microsoft account updates the existing row.
    CREATE UNIQUE INDEX UX_MicrosoftConnection_Account
        ON meta.MicrosoftConnection (TenantId, CreatedByUserId, Provider, MicrosoftUserId)
        WHERE IsDeleted = 0;
END
