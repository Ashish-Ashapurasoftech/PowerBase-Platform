-- Drop the upper bound restriction on Depth for the central execution queue
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CHK_PipelineQueue_Depth' AND parent_object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    ALTER TABLE meta.PipelineQueue DROP CONSTRAINT CHK_PipelineQueue_Depth;
END

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CHK_PipelineQueue_Depth' AND parent_object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    ALTER TABLE meta.PipelineQueue ADD CONSTRAINT CHK_PipelineQueue_Depth CHECK (Depth >= 1);
END
GO
