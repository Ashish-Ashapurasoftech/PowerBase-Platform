-- Saved PowerFlows connection accounts ("Connect new account").
--
-- Modelled on meta.AppToken (027_app_tokens.sql): the supplied user token is stored
-- as a SHA-256 hash plus a masked display prefix only. The raw token never enters
-- this table and is never returned by any API.
--
-- meta.PipelineConnection is deliberately NOT reused: it is a per-pipeline child row
-- (FK to meta.Pipeline) that CopyPipeline clones, so it cannot represent a
-- tenant-level account that is shared across pipelines and survives pipeline delete.
--
-- TargetTenantId / TargetUserId / UserTokenPublicId reference control-plane rows
-- (meta.Tenant, core.[User], core.UserToken) which do not exist in a tenant DB,
-- so no foreign keys are declared for them.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PipelineAccount' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.PipelineAccount (
        Id                BIGINT IDENTITY(1,1) PRIMARY KEY,
        PublicId          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId          BIGINT NOT NULL,
        CreatedByUserId   BIGINT NOT NULL,

        Name              NVARCHAR(200) NOT NULL,
        AuthMode          NVARCHAR(32) NOT NULL,
        Subdomain         NVARCHAR(100) NOT NULL,

        TargetTenantId    BIGINT NOT NULL,
        TargetUserId      BIGINT NOT NULL,

        UserTokenPublicId UNIQUEIDENTIFIER NULL,
        TokenHash         NVARCHAR(128) NULL,
        TokenPrefix       NVARCHAR(32) NULL,

        Status            NVARCHAR(32) NOT NULL DEFAULT 'active',
        IsActive          BIT NOT NULL DEFAULT 1,
        CreatedAt         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUsedAt        DATETIME2 NULL,
        IsDeleted         BIT NOT NULL DEFAULT 0,
        RowVersion        ROWVERSION NOT NULL,

        CONSTRAINT CK_PipelineAccount_AuthMode
            CHECK (AuthMode IN ('current_user', 'user_token')),

        CONSTRAINT CK_PipelineAccount_Status
            CHECK (Status IN ('active', 'revoked', 'unavailable')),

        -- A user_token account must carry its token identity; a current_user account must not.
        CONSTRAINT CK_PipelineAccount_TokenConsistency
            CHECK (
                (AuthMode = 'user_token'
                    AND TokenHash IS NOT NULL
                    AND UserTokenPublicId IS NOT NULL)
                OR
                (AuthMode = 'current_user'
                    AND TokenHash IS NULL
                    AND UserTokenPublicId IS NULL)
            )
    );

    CREATE UNIQUE INDEX UX_PipelineAccount_PublicId
        ON meta.PipelineAccount(PublicId);

    CREATE INDEX IX_PipelineAccount_TenantId_CreatedByUserId
        ON meta.PipelineAccount(TenantId, CreatedByUserId)
        WHERE IsDeleted = 0;

    -- One saved account per (tenant, owner, token): re-adding the same token reuses the
    -- owner's own row. Scoped by owner because accounts are listed per user, so two users
    -- holding the same token must not end up sharing (or stealing) one row.
    CREATE UNIQUE INDEX UX_PipelineAccount_TenantId_User_TokenHash
        ON meta.PipelineAccount(TenantId, CreatedByUserId, TokenHash)
        WHERE TokenHash IS NOT NULL AND IsDeleted = 0;
END
GO
