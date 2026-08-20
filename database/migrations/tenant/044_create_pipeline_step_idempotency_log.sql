-- Create Pipeline Step Idempotency Log table to support crash-safe and duplicate-safe cross-tenant executions
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PipelineStepIdempotencyLog' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.PipelineStepIdempotencyLog (
        MessageId UNIQUEIDENTIFIER NOT NULL,
        StepPublicId UNIQUEIDENTIFIER NOT NULL,
        ExecutionPathHash BINARY(32) NOT NULL,
        ExecutionPath NVARCHAR(MAX) NOT NULL,
        OutputJson NVARCHAR(MAX) NOT NULL,
        CreatedOn DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_PipelineStepIdempotencyLog
            PRIMARY KEY (MessageId, StepPublicId, ExecutionPathHash)
    );
END
GO
