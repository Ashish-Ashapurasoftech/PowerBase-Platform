-- Add TriggerSubtype column to meta.PipelineTriggerSubscription
IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription' AND c.name = 'TriggerSubtype')
    BEGIN
        ALTER TABLE meta.PipelineTriggerSubscription ADD TriggerSubtype VARCHAR(50) NOT NULL DEFAULT 'new-event';
    END
END
GO
