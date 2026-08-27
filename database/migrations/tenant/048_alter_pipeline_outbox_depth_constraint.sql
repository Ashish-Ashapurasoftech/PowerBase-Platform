-- Drop the upper bound restriction on Depth for the tenant-level outbox queue
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PipelineOutbox_Depth' AND parent_object_id = OBJECT_ID('meta.PipelineOutbox'))
BEGIN
    ALTER TABLE meta.PipelineOutbox DROP CONSTRAINT CK_PipelineOutbox_Depth;
END

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PipelineOutbox_Depth' AND parent_object_id = OBJECT_ID('meta.PipelineOutbox'))
BEGIN
    ALTER TABLE meta.PipelineOutbox ADD CONSTRAINT CK_PipelineOutbox_Depth CHECK (Depth >= 1);
END
GO
