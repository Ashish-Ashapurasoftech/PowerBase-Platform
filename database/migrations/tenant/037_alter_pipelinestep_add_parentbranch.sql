-- Tenant DB: add ParentBranch column to PipelineStep table.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineStep') AND name = 'ParentBranch')
BEGIN
    ALTER TABLE meta.PipelineStep ADD ParentBranch VARCHAR(50) NULL;
END
GO
