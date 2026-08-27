-- Tenant DB: create PipelineBulkEventRecord table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineBulkEventRecord')
BEGIN
    CREATE TABLE meta.PipelineBulkEventRecord (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        BulkEventId UNIQUEIDENTIFIER NOT NULL, -- Unique per matching subscription execution
        Ordinal INT NOT NULL,                  -- Preserves mutation order
        RecordPublicId UNIQUEIDENTIFIER NOT NULL,
        EventType VARCHAR(20) NOT NULL,        -- Added, Modified, Deleted
        BeforeValuesJson NVARCHAR(MAX) NULL,   -- Snapshot values before change
        AfterValuesJson NVARCHAR(MAX) NULL,    -- Snapshot values after change
        ChangedFieldsJson NVARCHAR(MAX) NULL,  -- JSON list of modified stable FIDs
        Processed TINYINT NOT NULL DEFAULT 0, -- 0 = Pending, 1 = Succeeded, 2 = Failed
        CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PipelineBulkEventRecord PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_PipelineBulkEventRecord_EventOrdinal UNIQUE (BulkEventId, Ordinal)
    );

    CREATE NONCLUSTERED INDEX IX_PipelineBulkEventRecord_Paging 
    ON meta.PipelineBulkEventRecord(BulkEventId, Processed, Ordinal) 
    INCLUDE (RecordPublicId, EventType);
END
GO
