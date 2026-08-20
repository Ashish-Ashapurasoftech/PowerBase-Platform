-- Tenant DB: add missing columns to PipelineStep table.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineStep') AND name = 'Label')
BEGIN
    ALTER TABLE meta.PipelineStep ADD Label NVARCHAR(200) NOT NULL DEFAULT '';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineStep') AND name = 'Notes')
BEGIN
    ALTER TABLE meta.PipelineStep ADD Notes NVARCHAR(500) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineStep') AND name = 'IsValidated')
BEGIN
    ALTER TABLE meta.PipelineStep ADD IsValidated BIT NOT NULL DEFAULT 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.PipelineStep') AND name = 'LastTriggeredOn')
BEGIN
    ALTER TABLE meta.PipelineStep ADD LastTriggeredOn DATETIME2(3) NULL;
END
GO
