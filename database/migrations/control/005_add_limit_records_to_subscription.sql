-- Add LimitRecords and MaxRecords columns to meta.PipelineTriggerSubscription
IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription' AND c.name = 'LimitRecords')
    BEGIN
        ALTER TABLE meta.PipelineTriggerSubscription ADD LimitRecords BIT NOT NULL DEFAULT 0;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineTriggerSubscription' AND c.name = 'MaxRecords')
    BEGIN
        ALTER TABLE meta.PipelineTriggerSubscription ADD MaxRecords INT NULL;
    END
END
GO
