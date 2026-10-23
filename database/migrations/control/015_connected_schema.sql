-- Connected Tables (standalone module), control-plane side: sharing consent between tenants, the registry of who reads
-- whose data, and the run dispatch queue. Own 'connected' schema; no existing table is altered.
IF SCHEMA_ID('connected') IS NULL EXEC('CREATE SCHEMA connected');
GO

-- Tenant sharing policy. A tenant with no row uses the defaults (approved_only / inbound enabled).
IF OBJECT_ID('connected.SharingPolicy') IS NULL
BEGIN
    CREATE TABLE connected.SharingPolicy (
        TenantId       BIGINT NOT NULL CONSTRAINT PK_ConnectedSharingPolicy PRIMARY KEY,
        OutboundPolicy NVARCHAR(16) NOT NULL CONSTRAINT DF_ConnectedSharingPolicy_Outbound DEFAULT 'approved_only', -- disabled | approved_only
        InboundEnabled BIT NOT NULL CONSTRAINT DF_ConnectedSharingPolicy_Inbound DEFAULT 1,
        ModifiedOn     DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedSharingPolicy_ModifiedOn DEFAULT SYSUTCDATETIME(),
        ModifiedBy     BIGINT NOT NULL,
        RowVersion     ROWVERSION NOT NULL,
        CONSTRAINT CK_ConnectedSharingPolicy_Outbound CHECK (OutboundPolicy IN ('disabled', 'approved_only'))
    );
END
GO

-- Consent: the provider tenant approves a consumer tenant (all apps or chosen apps).
IF OBJECT_ID('connected.DataShare') IS NULL
BEGIN
    CREATE TABLE connected.DataShare (
        Id                   BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedDataShare PRIMARY KEY,
        PublicId             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ConnectedDataShare_PublicId DEFAULT NEWID(),
        ProviderTenantId     BIGINT NOT NULL,
        ConsumerTenantId     BIGINT NOT NULL,
        Scope                NVARCHAR(10) NOT NULL CONSTRAINT DF_ConnectedDataShare_Scope DEFAULT 'all_apps', -- all_apps | apps
        AppPublicIdsJson     NVARCHAR(MAX) NULL,
        AllowEncryptedFields BIT NOT NULL CONSTRAINT DF_ConnectedDataShare_Encrypted DEFAULT 0,
        AllowCrossRegion     BIT NOT NULL CONSTRAINT DF_ConnectedDataShare_Region DEFAULT 0,
        Status               NVARCHAR(10) NOT NULL,                -- requested | approved | rejected | revoked
        RequestedByUserId    BIGINT NOT NULL,
        RequestedOn          DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedDataShare_RequestedOn DEFAULT SYSUTCDATETIME(),
        RequestNote          NVARCHAR(500) NULL,
        DecidedByUserId      BIGINT NULL,
        DecidedOn            DATETIME2(3) NULL,
        ExpiresOn            DATETIME2(3) NULL,
        RowVersion           ROWVERSION NOT NULL,
        CONSTRAINT CK_ConnectedDataShare_Scope CHECK (Scope IN ('all_apps', 'apps')),
        CONSTRAINT CK_ConnectedDataShare_Status CHECK (Status IN ('requested', 'approved', 'rejected', 'revoked'))
    );
    CREATE UNIQUE INDEX UX_ConnectedDataShare_PublicId ON connected.DataShare (PublicId);
    CREATE UNIQUE INDEX UX_ConnectedDataShare_Pair ON connected.DataShare (ProviderTenantId, ConsumerTenantId) WHERE Status IN ('requested', 'approved');
    CREATE INDEX IX_ConnectedDataShare_Consumer ON connected.DataShare (ConsumerTenantId, Status);
END
GO

-- Registry: lets the provider see (and revoke) every connected table that reads its data.
IF OBJECT_ID('connected.Link') IS NULL
BEGIN
    CREATE TABLE connected.Link (
        Id                             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedLink PRIMARY KEY,
        PublicId                       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ConnectedLink_PublicId DEFAULT NEWID(),
        ConsumerTenantId               BIGINT NOT NULL,
        ConsumerConnectedTablePublicId UNIQUEIDENTIFIER NOT NULL,
        ConsumerTableName              NVARCHAR(200) NOT NULL,
        ProviderTenantId               BIGINT NOT NULL,
        ProviderAppPublicId            UNIQUEIDENTIFIER NOT NULL,
        ProviderTablePublicId          UNIQUEIDENTIFIER NOT NULL,
        ProviderTableName              NVARCHAR(200) NOT NULL,
        RunAsUserId                    BIGINT NOT NULL,
        Status                         NVARCHAR(20) NOT NULL CONSTRAINT DF_ConnectedLink_Status DEFAULT 'active', -- active | revoked_by_provider | removed
        LastRunOn                      DATETIME2(3) NULL,
        LastRowCount                   BIGINT NULL,
        CreatedOn                      DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedLink_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ConnectedLink_Status CHECK (Status IN ('active', 'revoked_by_provider', 'removed'))
    );
    CREATE UNIQUE INDEX UX_ConnectedLink_PublicId ON connected.Link (PublicId);
    CREATE UNIQUE INDEX UX_ConnectedLink_Consumer ON connected.Link (ConsumerTenantId, ConsumerConnectedTablePublicId);
    CREATE INDEX IX_ConnectedLink_Provider ON connected.Link (ProviderTenantId, Status);
END
GO

-- Dispatch queue for refresh runs (the run itself lives in the tenant DB, connected.Run).
IF OBJECT_ID('connected.RunQueue') IS NULL
BEGIN
    CREATE TABLE connected.RunQueue (
        Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedRunQueue PRIMARY KEY,
        TenantId       BIGINT NOT NULL,
        RunPublicId    UNIQUEIDENTIFIER NOT NULL,
        SourceTenantId BIGINT NOT NULL,
        Status         NVARCHAR(12) NOT NULL CONSTRAINT DF_ConnectedRunQueue_Status DEFAULT 'Pending', -- Pending | Processing | Done | Failed
        ClaimToken     UNIQUEIDENTIFIER NULL,
        LockedBy       NVARCHAR(100) NULL,
        LockedUntil    DATETIME2(3) NULL,
        AttemptCount   INT NOT NULL CONSTRAINT DF_ConnectedRunQueue_Attempts DEFAULT 0,
        NextAttemptOn  DATETIME2(3) NULL,
        LastError      NVARCHAR(1000) NULL,
        CreatedOn      DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedRunQueue_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ConnectedRunQueue_Status CHECK (Status IN ('Pending', 'Processing', 'Done', 'Failed'))
    );
    CREATE UNIQUE INDEX UX_ConnectedRunQueue_Run ON connected.RunQueue (RunPublicId);
    CREATE INDEX IX_ConnectedRunQueue_Claim ON connected.RunQueue (Status, NextAttemptOn, CreatedOn) INCLUDE (TenantId, SourceTenantId, LockedUntil);
END
GO
