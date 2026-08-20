-- Tenant DB: add worker lease and attempt columns to PipelineRun, and create PipelineRunAttempt.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineRun') AND name = 'MessageId')
BEGIN
    ALTER TABLE audit.PipelineRun ADD MessageId UNIQUEIDENTIFIER NULL;
    ALTER TABLE audit.PipelineRun ADD AttemptCount INT NOT NULL DEFAULT 1;
    ALTER TABLE audit.PipelineRun ADD HeartbeatOn DATETIME2(3) NULL;
    ALTER TABLE audit.PipelineRun ADD LockedBy VARCHAR(100) NULL;
    ALTER TABLE audit.PipelineRun ADD LockedUntil DATETIME2(3) NULL;
    ALTER TABLE audit.PipelineRun ADD LastError NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PipelineRun_MessageId' AND object_id = OBJECT_ID('audit.PipelineRun'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_PipelineRun_MessageId ON audit.PipelineRun(MessageId) WHERE MessageId IS NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'audit' AND t.name = 'PipelineRunAttempt')
BEGIN
    CREATE TABLE audit.PipelineRunAttempt (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        PipelineRunId BIGINT NOT NULL,
        AttemptNumber INT NOT NULL,
        Status VARCHAR(20) NOT NULL, -- 'Running', 'Success', 'Failed'
        StartedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CompletedOn DATETIME2(3) NULL,
        LastError NVARCHAR(MAX) NULL,
        CONSTRAINT PK_PipelineRunAttempt PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineRunAttempt_Run FOREIGN KEY (PipelineRunId) REFERENCES audit.PipelineRun(Id) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'PipelineRunAttemptId')
BEGIN
    ALTER TABLE audit.PipelineStepRun ADD PipelineRunAttemptId BIGINT NULL;
    ALTER TABLE audit.PipelineStepRun ADD CONSTRAINT FK_PipelineStepRun_Attempt FOREIGN KEY (PipelineRunAttemptId) REFERENCES audit.PipelineRunAttempt(Id);
END
GO
