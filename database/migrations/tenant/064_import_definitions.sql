-- Table-to-Table / file Import: saved, re-runnable Import Definitions and their append-only run log.
IF OBJECT_ID('meta.ImportDefinition') IS NULL
BEGIN
    CREATE TABLE meta.ImportDefinition (
        Id                 BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportDefinition PRIMARY KEY,
        PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ImportDefinition_PublicId DEFAULT NEWID(),
        AppId              BIGINT NOT NULL,
        DestinationTableId BIGINT NOT NULL CONSTRAINT FK_ImportDefinition_DestTable REFERENCES meta.AppTable(Id),
        SourceKind         NVARCHAR(10) NOT NULL CONSTRAINT DF_ImportDefinition_SourceKind DEFAULT 'table',
        SourceTableId      BIGINT NULL CONSTRAINT FK_ImportDefinition_SourceTable REFERENCES meta.AppTable(Id),
        Name               NVARCHAR(200) NOT NULL,
        ImportType         NVARCHAR(10) NOT NULL,           -- copy | merge
        MergeKeyFid        INT NULL,
        ConditionsJson     NVARCHAR(MAX) NULL,              -- FilterGroup JSON (source filter)
        FieldMappingJson   NVARCHAR(MAX) NOT NULL,
        ColumnRulesJson    NVARCHAR(MAX) NULL,
        OptionsJson        NVARCHAR(MAX) NULL,              -- constraint policy, notification recipients, ...
        ScheduleJson       NVARCHAR(MAX) NULL,
        NextRunOn          DATETIME2(3) NULL,
        RunAsUserId        BIGINT NOT NULL,
        NeedsAttention     BIT NOT NULL CONSTRAINT DF_ImportDefinition_NeedsAttention DEFAULT 0,
        AttentionReason    NVARCHAR(500) NULL,
        IsDeleted          BIT NOT NULL CONSTRAINT DF_ImportDefinition_IsDeleted DEFAULT 0,
        CreatedOn          DATETIME2(3) NOT NULL CONSTRAINT DF_ImportDefinition_CreatedOn DEFAULT SYSUTCDATETIME(),
        CreatedBy          BIGINT NOT NULL,
        ModifiedOn         DATETIME2(3) NULL,
        ModifiedBy         BIGINT NULL,
        CONSTRAINT CK_ImportDefinition_Type CHECK (ImportType IN ('copy', 'merge')),
        CONSTRAINT CK_ImportDefinition_Source CHECK (SourceKind IN ('table', 'file'))
    );
    CREATE UNIQUE INDEX UX_ImportDefinition_PublicId ON meta.ImportDefinition (PublicId);
    CREATE INDEX IX_ImportDefinition_DestTable ON meta.ImportDefinition (DestinationTableId) WHERE IsDeleted = 0;
    CREATE INDEX IX_ImportDefinition_Schedule ON meta.ImportDefinition (NextRunOn) WHERE IsDeleted = 0 AND NextRunOn IS NOT NULL;
END
GO

IF OBJECT_ID('meta.ImportRun') IS NULL
BEGIN
    CREATE TABLE meta.ImportRun (
        Id                     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportRun PRIMARY KEY,
        PublicId               UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_ImportRun_PublicId DEFAULT NEWID(),
        ImportDefinitionId     BIGINT NOT NULL CONSTRAINT FK_ImportRun_Definition REFERENCES meta.ImportDefinition(Id),
        TriggeredBy            NVARCHAR(10) NOT NULL,       -- manual | schedule | api
        TriggeredByUserId      BIGINT NOT NULL,
        Status                 NVARCHAR(12) NOT NULL,       -- queued | running | success | partial | failed | cancelled
        Progress               TINYINT NOT NULL CONSTRAINT DF_ImportRun_Progress DEFAULT 0,
        RowsRead               BIGINT NOT NULL CONSTRAINT DF_ImportRun_RowsRead DEFAULT 0,
        Inserted               BIGINT NOT NULL CONSTRAINT DF_ImportRun_Inserted DEFAULT 0,
        Updated                BIGINT NOT NULL CONSTRAINT DF_ImportRun_Updated DEFAULT 0,
        Skipped                BIGINT NOT NULL CONSTRAINT DF_ImportRun_Skipped DEFAULT 0,
        Errored                BIGINT NOT NULL CONSTRAINT DF_ImportRun_Errored DEFAULT 0,
        LastCommittedSourceId  BIGINT NOT NULL CONSTRAINT DF_ImportRun_Cursor DEFAULT 0,   -- resume cursor
        SourceMaxId            BIGINT NULL,                 -- upper bound of the source range, pinned when the run starts
        StartedOn              DATETIME2(3) NULL,
        CompletedOn            DATETIME2(3) NULL,
        ErrorDetail            NVARCHAR(1000) NULL,
        FeedbackFileUrl        NVARCHAR(500) NULL,
        DefinitionSnapshotJson NVARCHAR(MAX) NOT NULL,      -- definition as it was when the run was queued
        CreatedOn              DATETIME2(3) NOT NULL CONSTRAINT DF_ImportRun_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ImportRun_Status CHECK (Status IN ('queued', 'running', 'success', 'partial', 'failed', 'cancelled')),
        CONSTRAINT CK_ImportRun_TriggeredBy CHECK (TriggeredBy IN ('manual', 'schedule', 'api'))
    );
    CREATE UNIQUE INDEX UX_ImportRun_PublicId ON meta.ImportRun (PublicId);
    CREATE INDEX IX_ImportRun_Definition ON meta.ImportRun (ImportDefinitionId, Id DESC);
END
GO

IF OBJECT_ID('meta.ImportRunIssue') IS NULL
BEGIN
    CREATE TABLE meta.ImportRunIssue (
        Id           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportRunIssue PRIMARY KEY,
        ImportRunId  BIGINT NOT NULL CONSTRAINT FK_ImportRunIssue_Run REFERENCES meta.ImportRun(Id),
        SourceRowRef BIGINT NULL,                            -- source Record ID# (table) or file row number
        ColumnFid    INT NULL,
        Outcome      NVARCHAR(10) NOT NULL,                  -- skipped | errored
        ReasonCode   NVARCHAR(40) NOT NULL,
        Message      NVARCHAR(500) NOT NULL
    );
    CREATE INDEX IX_ImportRunIssue_Run ON meta.ImportRunIssue (ImportRunId, Id);
END
GO
