-- Add IsSimpleFilter and AdvancedQuery columns to meta.PipelineTriggerSubscription so "On New Event"
-- can persist and evaluate the Quickbase advanced-query filter mode, not just Simple Filter groups.
IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription' AND c.name = 'IsSimpleFilter')
    BEGIN
        ALTER TABLE meta.PipelineTriggerSubscription ADD IsSimpleFilter BIT NOT NULL DEFAULT 1;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription' AND c.name = 'AdvancedQuery')
    BEGIN
        ALTER TABLE meta.PipelineTriggerSubscription ADD AdvancedQuery NVARCHAR(MAX) NULL;
    END
END
GO
