-- Callable invocations use the existing durable queue without record-trigger subscriptions.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('meta.PipelineQueue') AND name = 'CHK_PipelineQueue_QueueSource')
    ALTER TABLE meta.PipelineQueue DROP CONSTRAINT CHK_PipelineQueue_QueueSource;
GO
ALTER TABLE meta.PipelineQueue WITH CHECK ADD CONSTRAINT CHK_PipelineQueue_QueueSource
    CHECK (QueueSource IN ('Event', 'Manual', 'Schedule', 'Webhook', 'Callable'));
GO
