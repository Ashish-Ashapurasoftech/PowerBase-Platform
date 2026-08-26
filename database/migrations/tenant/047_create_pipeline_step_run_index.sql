-- Tenant DB: create index on audit.PipelineStepRun(PipelineRunId, StartedOn, Id)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineStepRun_PipelineRunId' AND object_id = OBJECT_ID('audit.PipelineStepRun'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineStepRun_PipelineRunId 
    ON audit.PipelineStepRun(PipelineRunId, StartedOn, Id);
END
GO
