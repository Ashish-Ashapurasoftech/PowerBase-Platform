-- Add PausedNextAttemptOn column to meta.PipelineQueue
IF NOT EXISTS (
    SELECT 1 
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'PipelineQueue' AND c.name = 'PausedNextAttemptOn'
)
BEGIN
    ALTER TABLE meta.PipelineQueue
    ADD PausedNextAttemptOn DATETIME2(3) NULL;
END
GO
