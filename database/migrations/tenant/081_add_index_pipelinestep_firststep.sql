-- Covering index for the PowerFlows list "first step" lookup (Type/Subtype of the first
-- top-level step per pipeline, ordered by DisplayOrder). Lets the list query seek per pipeline
-- instead of reading full PipelineStep rows.
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineStep_FirstStep' AND object_id = OBJECT_ID('meta.PipelineStep'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineStep_FirstStep
        ON meta.PipelineStep(PipelineId, DisplayOrder)
        INCLUDE (Type, Subtype)
        WHERE IsDeleted = 0 AND ParentStepId IS NULL;
END
GO
