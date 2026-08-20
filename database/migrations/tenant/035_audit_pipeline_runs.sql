-- Tenant DB: create Pipeline audit execution log tables.
-- 1. Create Pipeline Runs Table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'PipelineRun')
BEGIN
    CREATE TABLE audit.PipelineRun (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        PipelineId  BIGINT        NOT NULL,
        Status      VARCHAR(20)   NOT NULL,
        TriggerType VARCHAR(30)   NOT NULL,
        StartedOn   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CompletedOn DATETIME2(3)  NULL,
        TriggeredBy BIGINT        NOT NULL DEFAULT 0,
        ErrorMessage NVARCHAR(MAX) NULL,
        CONSTRAINT PK_PipelineRun PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineRun_Pipeline FOREIGN KEY (PipelineId) REFERENCES meta.Pipeline(Id)
    );
    CREATE NONCLUSTERED INDEX IX_PipelineRun_PipelineId ON audit.PipelineRun(PipelineId);
END
GO

-- 2. Create Step Execution Logs Table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'PipelineStepRun')
BEGIN
    CREATE TABLE audit.PipelineStepRun (
        Id            BIGINT IDENTITY(1,1) NOT NULL,
        PipelineRunId BIGINT        NOT NULL,
        StepId        BIGINT        NOT NULL,
        Status        VARCHAR(20)   NOT NULL,
        StartedOn     DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CompletedOn   DATETIME2(3)  NULL,
        InputContext  NVARCHAR(MAX) NULL,
        OutputContext NVARCHAR(MAX) NULL,
        LogMessage    NVARCHAR(MAX) NULL,
        CONSTRAINT PK_PipelineStepRun PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineStepRun_Run FOREIGN KEY (PipelineRunId) REFERENCES audit.PipelineRun(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PipelineStepRun_Step FOREIGN KEY (StepId) REFERENCES meta.PipelineStep(Id)
    );
END
GO
