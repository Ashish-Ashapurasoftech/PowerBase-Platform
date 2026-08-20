-- Tenant DB: create Pipeline metadata tables.
-- 1. Create Pipelines Table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Pipeline')
BEGIN
    CREATE TABLE meta.Pipeline (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        PublicId      UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId         BIGINT        NOT NULL,
        Name          NVARCHAR(200) NOT NULL,
        Description   NVARCHAR(500) NULL,
        VariablesJson NVARCHAR(MAX) NULL, -- Global variables
        IsActive      BIT           NOT NULL DEFAULT 1,
        IsDeleted     BIT           NOT NULL DEFAULT 0,
        CreatedOn     DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy     BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn    DATETIME2(3)  NULL,
        ModifiedBy    BIGINT        NULL,
        DeletedOn     DATETIME2(3)  NULL,
        DeletedBy     BIGINT        NULL,
        RowVersion    ROWVERSION    NOT NULL,
        CONSTRAINT PK_Pipeline PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX IX_Pipeline_AppId ON meta.Pipeline(AppId) WHERE IsDeleted = 0;
END
GO

-- 2. Create Pipeline Connections Table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineConnection')
BEGIN
    CREATE TABLE meta.PipelineConnection (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        PublicId        UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        PipelineId      BIGINT        NOT NULL,
        Name            NVARCHAR(200) NOT NULL,
        Type            VARCHAR(50)   NOT NULL, -- 'quickbase', 'outlook', etc.
        CredentialsJson NVARCHAR(MAX) NOT NULL, -- encrypted credentials
        IsDeleted       BIT           NOT NULL DEFAULT 0,
        CreatedOn       DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy       BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn      DATETIME2(3)  NULL,
        ModifiedBy      BIGINT        NULL,
        DeletedOn       DATETIME2(3)  NULL,
        DeletedBy       BIGINT        NULL,
        RowVersion      ROWVERSION    NOT NULL,
        CONSTRAINT PK_PipelineConnection PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineConnection_Pipeline FOREIGN KEY (PipelineId) REFERENCES meta.Pipeline(Id)
    );
    CREATE NONCLUSTERED INDEX IX_PipelineConnection_PipelineId ON meta.PipelineConnection(PipelineId) WHERE IsDeleted = 0;
END
GO

-- 3. Create Pipeline Steps Table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineStep')
BEGIN
    CREATE TABLE meta.PipelineStep (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        PublicId      UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        PipelineId    BIGINT        NOT NULL,
        ParentStepId  BIGINT        NULL,
        ParentBranch  VARCHAR(50)   NULL,
        RefId         VARCHAR(50)   NOT NULL, -- 'steps.ac', etc.
        DisplayOrder  INT           NOT NULL DEFAULT 0,
        Type          VARCHAR(50)   NOT NULL, -- 'trigger', 'action', 'query', 'control'
        Subtype       VARCHAR(50)   NOT NULL, -- 'record-added', 'send-email-outlook', etc.
        ConfigJson    NVARCHAR(MAX) NULL, -- config fields
        IsDeleted     BIT           NOT NULL DEFAULT 0,
        CreatedOn     DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy     BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn    DATETIME2(3)  NULL,
        ModifiedBy    BIGINT        NULL,
        DeletedOn     DATETIME2(3)  NULL,
        DeletedBy     BIGINT        NULL,
        RowVersion    ROWVERSION    NOT NULL,
        CONSTRAINT PK_PipelineStep PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineStep_Pipeline FOREIGN KEY (PipelineId) REFERENCES meta.Pipeline(Id),
        CONSTRAINT FK_PipelineStep_Parent FOREIGN KEY (ParentStepId) REFERENCES meta.PipelineStep(Id)
    );
    CREATE NONCLUSTERED INDEX IX_PipelineStep_PipelineId ON meta.PipelineStep(PipelineId) WHERE IsDeleted = 0;
END
GO
