-- Connected Tables (standalone module): everything lives in its own 'connected' schema. No existing table is altered;
-- the destination table itself is an ordinary meta.AppTable + data.t_{id} created through the normal table/field handlers.
IF SCHEMA_ID('connected') IS NULL EXEC('CREATE SCHEMA connected');
GO

-- A saved credential to read one source app. Separate from PowerFlows' meta.PipelineAccount on purpose.
IF OBJECT_ID('connected.[Connection]') IS NULL
BEGIN
    CREATE TABLE connected.[Connection] (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedConnection PRIMARY KEY,
        PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ConnectedConnection_PublicId DEFAULT NEWID(),
        OwnerUserId       BIGINT NOT NULL,
        Name              NVARCHAR(200) NOT NULL,
        AuthMode          NVARCHAR(16) NOT NULL,                  -- same_realm_user | user_token
        RealmSlug         NVARCHAR(100) NOT NULL,
        SourceTenantId    BIGINT NOT NULL,                        -- resolved from RealmSlug when created, then pinned
        SourceAppPublicId UNIQUEIDENTIFIER NOT NULL,              -- one connection reads one app
        SourceAppName     NVARCHAR(200) NOT NULL,                 -- display cache
        RunAsUserId       BIGINT NOT NULL,                        -- token owner, or OwnerUserId for same_realm_user
        UserTokenHash     NVARCHAR(64) NULL,                      -- SHA-256 hex; the raw token is never stored
        UserTokenPrefix   NVARCHAR(20) NULL,
        AppTokenHash      NVARCHAR(64) NULL,
        Status            NVARCHAR(12) NOT NULL CONSTRAINT DF_ConnectedConnection_Status DEFAULT 'active', -- active | revoked | unavailable
        StatusReason      NVARCHAR(500) NULL,
        LastVerifiedOn    DATETIME2(3) NULL,
        LastUsedOn        DATETIME2(3) NULL,
        IsDeleted         BIT NOT NULL CONSTRAINT DF_ConnectedConnection_IsDeleted DEFAULT 0,
        CreatedOn         DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedConnection_CreatedOn DEFAULT SYSUTCDATETIME(),
        ModifiedOn        DATETIME2(3) NULL,
        RowVersion        ROWVERSION NOT NULL,
        CONSTRAINT CK_ConnectedConnection_AuthMode CHECK (AuthMode IN ('same_realm_user', 'user_token')),
        CONSTRAINT CK_ConnectedConnection_Status CHECK (Status IN ('active', 'revoked', 'unavailable'))
    );
    CREATE UNIQUE INDEX UX_ConnectedConnection_PublicId ON connected.[Connection] (PublicId);
    CREATE INDEX IX_ConnectedConnection_Owner ON connected.[Connection] (OwnerUserId) WHERE IsDeleted = 0;
END
GO

IF OBJECT_ID('connected.ConnectedTable') IS NULL
BEGIN
    CREATE TABLE connected.ConnectedTable (
        Id                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedTable PRIMARY KEY,
        PublicId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ConnectedTable_PublicId DEFAULT NEWID(),
        AppId               BIGINT NOT NULL,                       -- destination app (meta.App.Id, referenced, not altered)
        AppTableId          BIGINT NOT NULL,                       -- destination table (meta.AppTable.Id)
        ConnectionId        BIGINT NOT NULL CONSTRAINT FK_ConnectedTable_Connection REFERENCES connected.[Connection](Id),
        OwnerUserId         BIGINT NOT NULL,
        SourceTenantId      BIGINT NOT NULL,
        SourceAppPublicId   UNIQUEIDENTIFIER NOT NULL,
        SourceTablePublicId UNIQUEIDENTIFIER NOT NULL,
        SourceTableName     NVARCHAR(200) NOT NULL,
        IsCrossTenant       BIT NOT NULL,
        RefreshKeySourceFid INT NOT NULL CONSTRAINT DF_ConnectedTable_RefreshKey DEFAULT 3,   -- 3 = source Record ID#
        RefreshKeyIsAuto    BIT NOT NULL CONSTRAINT DF_ConnectedTable_RefreshKeyAuto DEFAULT 1,
        UpdateMode          NVARCHAR(10) NOT NULL,                 -- mirror | append
        FilterEnabled       BIT NOT NULL CONSTRAINT DF_ConnectedTable_FilterEnabled DEFAULT 0,
        FilterJson          NVARCHAR(MAX) NULL,                    -- FilterGroup over source Fids
        ScheduleJson        NVARCHAR(MAX) NULL,
        NextRunOn           DATETIME2(3) NULL,
        TimeZone            NVARCHAR(64) NOT NULL CONSTRAINT DF_ConnectedTable_TimeZone DEFAULT 'UTC',
        NotifyOnFailure     BIT NOT NULL CONSTRAINT DF_ConnectedTable_Notify DEFAULT 1,
        MassDeleteGuardPct  TINYINT NOT NULL CONSTRAINT DF_ConnectedTable_Guard DEFAULT 50,
        ConsecutiveFailures INT NOT NULL CONSTRAINT DF_ConnectedTable_Failures DEFAULT 0,
        MappingVersion      INT NOT NULL CONSTRAINT DF_ConnectedTable_MappingVersion DEFAULT 1,
        FullRefreshVersion  INT NOT NULL CONSTRAINT DF_ConnectedTable_FullRefresh DEFAULT 1,     -- mapping/filter/key version the watermark belongs to
        LastSourceWatermark DATETIME2(3) NULL,                     -- incremental refresh: source Date Modified high-water mark
        WatermarkVersion    INT NULL,
        IncrementalRunCount INT NOT NULL CONSTRAINT DF_ConnectedTable_IncrRuns DEFAULT 0,
        Status              NVARCHAR(16) NOT NULL CONSTRAINT DF_ConnectedTable_Status DEFAULT 'active', -- active | paused | needs_attention | held | removed
        AttentionReason     NVARCHAR(500) NULL,
        LastSuccessfulRunOn DATETIME2(3) NULL,
        IsDeleted           BIT NOT NULL CONSTRAINT DF_ConnectedTable_IsDeleted DEFAULT 0,
        CreatedOn           DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedTable_CreatedOn DEFAULT SYSUTCDATETIME(),
        CreatedBy           BIGINT NOT NULL,
        ModifiedOn          DATETIME2(3) NULL,
        ModifiedBy          BIGINT NULL,
        RowVersion          ROWVERSION NOT NULL,
        CONSTRAINT CK_ConnectedTable_UpdateMode CHECK (UpdateMode IN ('mirror', 'append')),
        CONSTRAINT CK_ConnectedTable_Status CHECK (Status IN ('active', 'paused', 'needs_attention', 'held', 'removed'))
    );
    CREATE UNIQUE INDEX UX_ConnectedTable_PublicId ON connected.ConnectedTable (PublicId);
    CREATE UNIQUE INDEX UX_ConnectedTable_Table ON connected.ConnectedTable (AppTableId) WHERE IsDeleted = 0;
    CREATE INDEX IX_ConnectedTable_App ON connected.ConnectedTable (AppId) WHERE IsDeleted = 0;
    CREATE INDEX IX_ConnectedTable_Due ON connected.ConnectedTable (NextRunOn) WHERE IsDeleted = 0 AND NextRunOn IS NOT NULL;
END
GO

IF OBJECT_ID('connected.ConnectedField') IS NULL
BEGIN
    CREATE TABLE connected.ConnectedField (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedField PRIMARY KEY,
        ConnectedTableId BIGINT NOT NULL CONSTRAINT FK_ConnectedField_Table REFERENCES connected.ConnectedTable(Id),
        AppFieldId       BIGINT NOT NULL,                          -- destination field (meta.AppField.Id)
        SourceFid        INT NOT NULL,
        SourceFieldName  NVARCHAR(200) NOT NULL,
        SourceTypeCode   NVARCHAR(40) NOT NULL,
        Conversion       NVARCHAR(30) NOT NULL,
        SortOrder        INT NOT NULL,
        State            NVARCHAR(16) NOT NULL CONSTRAINT DF_ConnectedField_State DEFAULT 'connected', -- connected | source_missing | type_conflict
        IsDeleted        BIT NOT NULL CONSTRAINT DF_ConnectedField_IsDeleted DEFAULT 0,
        CONSTRAINT CK_ConnectedField_State CHECK (State IN ('connected', 'source_missing', 'type_conflict'))
    );
    CREATE INDEX IX_ConnectedField_Table ON connected.ConnectedField (ConnectedTableId) WHERE IsDeleted = 0;
END
GO

-- Key map: which local record holds which source record (instead of adding columns to data.t_{id}).
IF OBJECT_ID('connected.SyncRow') IS NULL
BEGIN
    CREATE TABLE connected.SyncRow (
        ConnectedTableId BIGINT NOT NULL,
        SyncKey          NVARCHAR(450) NOT NULL,
        LocalRecordId    BIGINT NOT NULL,
        LocalPublicId    UNIQUEIDENTIFIER NOT NULL,
        SourceHash       BINARY(32) NOT NULL,
        LastSeenRunId    BIGINT NOT NULL,
        CONSTRAINT PK_ConnectedSyncRow PRIMARY KEY (ConnectedTableId, SyncKey)
    );
    CREATE INDEX IX_ConnectedSyncRow_Run ON connected.SyncRow (ConnectedTableId, LastSeenRunId);
    CREATE UNIQUE INDEX UX_ConnectedSyncRow_Local ON connected.SyncRow (ConnectedTableId, LocalRecordId);
END
GO

IF OBJECT_ID('connected.Run') IS NULL
BEGIN
    CREATE TABLE connected.Run (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedRun PRIMARY KEY,
        PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ConnectedRun_PublicId DEFAULT NEWID(),
        ConnectedTableId  BIGINT NOT NULL CONSTRAINT FK_ConnectedRun_Table REFERENCES connected.ConnectedTable(Id),
        TriggeredBy       NVARCHAR(12) NOT NULL,                   -- manual | schedule | api | retry
        TriggeredByUserId BIGINT NOT NULL,
        Status            NVARCHAR(12) NOT NULL,                   -- queued | running | success | partial | held | failed | cancelled
        IsIncremental     BIT NOT NULL CONSTRAINT DF_ConnectedRun_Incremental DEFAULT 0,
        ConfirmMassDelete BIT NOT NULL CONSTRAINT DF_ConnectedRun_Confirm DEFAULT 0,
        CancelRequested   BIT NOT NULL CONSTRAINT DF_ConnectedRun_Cancel DEFAULT 0,
        RowsRead          BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_RowsRead DEFAULT 0,
        Inserted          BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Inserted DEFAULT 0,
        Updated           BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Updated DEFAULT 0,
        Unchanged         BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Unchanged DEFAULT 0,
        Reverted          BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Reverted DEFAULT 0,
        Deleted           BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Deleted DEFAULT 0,
        Skipped           BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Skipped DEFAULT 0,
        Errored           BIGINT NOT NULL CONSTRAINT DF_ConnectedRun_Errored DEFAULT 0,
        HeldDeleteCount   BIGINT NULL,                             -- set when the mass-delete guard held the run
        SourceMaxId       BIGINT NULL,
        SnapshotJson      NVARCHAR(MAX) NOT NULL,                  -- the configuration as it was when queued
        ErrorDetail       NVARCHAR(1000) NULL,
        QueuedOn          DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedRun_QueuedOn DEFAULT SYSUTCDATETIME(),
        StartedOn         DATETIME2(3) NULL,
        CompletedOn       DATETIME2(3) NULL,
        CONSTRAINT CK_ConnectedRun_Status CHECK (Status IN ('queued', 'running', 'success', 'partial', 'held', 'failed', 'cancelled')),
        CONSTRAINT CK_ConnectedRun_Trigger CHECK (TriggeredBy IN ('manual', 'schedule', 'api', 'retry'))
    );
    CREATE UNIQUE INDEX UX_ConnectedRun_PublicId ON connected.Run (PublicId);
    CREATE INDEX IX_ConnectedRun_Table ON connected.Run (ConnectedTableId, Id DESC);
    -- At most one queued or running refresh per connected table: a second Refresh is refused by the database itself.
    CREATE UNIQUE INDEX UX_ConnectedRun_Active ON connected.Run (ConnectedTableId) WHERE Status IN ('queued', 'running');
END
GO

IF OBJECT_ID('connected.RunIssue') IS NULL
BEGIN
    CREATE TABLE connected.RunIssue (
        Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedRunIssue PRIMARY KEY,
        RunId          BIGINT NOT NULL CONSTRAINT FK_ConnectedRunIssue_Run REFERENCES connected.Run(Id),
        SourceRecordId BIGINT NULL,
        SourceFid      INT NULL,
        Outcome        NVARCHAR(10) NOT NULL,                      -- skipped | errored | warning
        ReasonCode     NVARCHAR(40) NOT NULL,
        Message        NVARCHAR(500) NOT NULL                      -- never contains a field value
    );
    CREATE INDEX IX_ConnectedRunIssue_Run ON connected.RunIssue (RunId, Id);
END
GO

-- "Refresh Connected Data" per role, kept out of meta.AppRoleTablePermission.
IF OBJECT_ID('connected.RefreshPermission') IS NULL
BEGIN
    CREATE TABLE connected.RefreshPermission (
        ConnectedTableId BIGINT NOT NULL CONSTRAINT FK_ConnectedRefreshPermission_Table REFERENCES connected.ConnectedTable(Id),
        AppRoleId        BIGINT NOT NULL,
        CONSTRAINT PK_ConnectedRefreshPermission PRIMARY KEY (ConnectedTableId, AppRoleId)
    );
END
GO
