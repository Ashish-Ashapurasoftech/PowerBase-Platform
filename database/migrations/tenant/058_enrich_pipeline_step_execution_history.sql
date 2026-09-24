-- Quickbase-parity execution diagnostics for every individual step invocation.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'ExecutionPath')
    ALTER TABLE audit.PipelineStepRun ADD ExecutionPath NVARCHAR(1000) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'SequenceNumber')
    ALTER TABLE audit.PipelineStepRun ADD SequenceNumber INT NOT NULL CONSTRAINT DF_PipelineStepRun_SequenceNumber DEFAULT 0;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'TransactionOutcome')
    ALTER TABLE audit.PipelineStepRun ADD TransactionOutcome VARCHAR(30) NOT NULL CONSTRAINT DF_PipelineStepRun_TransactionOutcome DEFAULT 'NotApplicable';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'ErrorType')
    ALTER TABLE audit.PipelineStepRun ADD ErrorType NVARCHAR(300) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineStepRun_RunSequence' AND object_id = OBJECT_ID('audit.PipelineStepRun'))
    CREATE NONCLUSTERED INDEX IX_PipelineStepRun_RunSequence
        ON audit.PipelineStepRun(PipelineRunId, SequenceNumber, Id)
        INCLUDE (Status, StartedOn, CompletedOn, PipelineRunAttemptId);
GO
