IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='meta' AND t.name='PipelineSearchWorkset')
BEGIN
    CREATE TABLE meta.PipelineSearchWorkset (
        WorksetId UNIQUEIDENTIFIER NOT NULL,
        RunMessageId UNIQUEIDENTIFIER NOT NULL,
        StepRefId NVARCHAR(100) NOT NULL,
        Status VARCHAR(20) NOT NULL DEFAULT 'Discovering',
        LastRecordId BIGINT NOT NULL DEFAULT 0,
        SnapshotMaxRecordId BIGINT NOT NULL,
        DiscoveredCount INT NOT NULL DEFAULT 0,
        CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        DiscoveryCompletedOn DATETIME2(3) NULL,
        CONSTRAINT PK_PipelineSearchWorkset PRIMARY KEY (WorksetId),
        CONSTRAINT UX_PipelineSearchWorkset_RunStep UNIQUE (RunMessageId, StepRefId),
        CONSTRAINT CK_PipelineSearchWorkset_Status CHECK (Status IN ('Discovering','Ready','Completed'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PipelineSearchWorkset_Run' AND object_id=OBJECT_ID('meta.PipelineSearchWorkset'))
    CREATE INDEX IX_PipelineSearchWorkset_Run ON meta.PipelineSearchWorkset(RunMessageId, Status);
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID('meta.PipelineBulkEventRecord') AND name='SearchWorksetId')
    ALTER TABLE meta.PipelineBulkEventRecord ADD SearchWorksetId UNIQUEIDENTIFIER NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_PipelineBulkEventRecord_SearchWorksetOrdinal' AND object_id=OBJECT_ID('meta.PipelineBulkEventRecord'))
    CREATE UNIQUE INDEX UX_PipelineBulkEventRecord_SearchWorksetOrdinal
    ON meta.PipelineBulkEventRecord(SearchWorksetId, Ordinal) WHERE SearchWorksetId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_PipelineBulkEventRecord_SearchWorksetPending' AND object_id=OBJECT_ID('meta.PipelineBulkEventRecord'))
    CREATE INDEX IX_PipelineBulkEventRecord_SearchWorksetPending
    ON meta.PipelineBulkEventRecord(SearchWorksetId, Processed, Ordinal)
    INCLUDE (RecordPublicId, AfterValuesJson) WHERE SearchWorksetId IS NOT NULL;
GO
