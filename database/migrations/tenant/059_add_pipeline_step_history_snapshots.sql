-- Keep execution history immutable when a pipeline step is later renamed or edited.
-- This is intentionally a separate migration because 058 may already be recorded as applied.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.PipelineStepRun') AND name = 'StepPublicIdSnapshot')
BEGIN
    ALTER TABLE audit.PipelineStepRun ADD
        StepPublicIdSnapshot UNIQUEIDENTIFIER NULL,
        StepRefIdSnapshot NVARCHAR(100) NULL,
        StepLabelSnapshot NVARCHAR(500) NULL,
        StepTypeSnapshot NVARCHAR(100) NULL,
        StepSubtypeSnapshot NVARCHAR(100) NULL;
END
GO

UPDATE sr
   SET StepPublicIdSnapshot = COALESCE(sr.StepPublicIdSnapshot, s.PublicId),
       StepRefIdSnapshot = COALESCE(sr.StepRefIdSnapshot, s.RefId),
       StepLabelSnapshot = COALESCE(sr.StepLabelSnapshot, NULLIF(s.Label, ''), s.RefId),
       StepTypeSnapshot = COALESCE(sr.StepTypeSnapshot, s.Type),
       StepSubtypeSnapshot = COALESCE(sr.StepSubtypeSnapshot, s.Subtype)
  FROM audit.PipelineStepRun sr
  JOIN meta.PipelineStep s ON s.Id = sr.StepId
 WHERE sr.StepPublicIdSnapshot IS NULL
    OR sr.StepRefIdSnapshot IS NULL
    OR sr.StepLabelSnapshot IS NULL
    OR sr.StepTypeSnapshot IS NULL
    OR sr.StepSubtypeSnapshot IS NULL;
GO
