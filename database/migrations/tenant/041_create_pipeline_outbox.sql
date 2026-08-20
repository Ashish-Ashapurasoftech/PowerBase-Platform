-- Tenant DB: create PipelineOutbox table.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineOutbox')
BEGIN
    CREATE TABLE meta.PipelineOutbox (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        PipelineId BIGINT NOT NULL,
        TriggerEvent VARCHAR(50) NOT NULL,
        TriggerPayloadJson NVARCHAR(MAX) NOT NULL,
        TriggeredBy BIGINT NOT NULL DEFAULT 0,
        TriggerTablePublicId UNIQUEIDENTIFIER NOT NULL,
        CorrelationId UNIQUEIDENTIFIER NOT NULL,
        Depth INT NOT NULL DEFAULT 1,
        PipelineChain NVARCHAR(1000) NOT NULL DEFAULT '[]',
        MessageId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        BatchId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        PayloadVersion VARCHAR(10) NOT NULL DEFAULT '1.0',
        CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        Published TINYINT NOT NULL DEFAULT 0, -- 0 = Pending, 1 = Published, 2 = Failed/Dead Letter
        PublishedOn DATETIME2(3) NULL,
        FailedOn DATETIME2(3) NULL,
        AttemptCount INT NOT NULL DEFAULT 0,
        NextAttemptOn DATETIME2(3) NULL,
        LastError NVARCHAR(MAX) NULL,
        LockedBy VARCHAR(100) NULL,
        LockedUntil DATETIME2(3) NULL,
        CONSTRAINT PK_PipelineOutbox PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_PipelineOutbox_Pipeline FOREIGN KEY (PipelineId) REFERENCES meta.Pipeline(Id),
        CONSTRAINT CK_PipelineOutbox_Published CHECK (Published IN (0, 1, 2)),
        CONSTRAINT CK_PipelineOutbox_Depth CHECK (Depth >= 1 AND Depth <= 11),
        CONSTRAINT CK_PipelineOutbox_AttemptCount CHECK (AttemptCount >= 0 AND AttemptCount <= 5)
    );

    CREATE NONCLUSTERED INDEX IX_PipelineOutbox_Claim ON meta.PipelineOutbox(Published, LockedUntil, NextAttemptOn) WHERE Published = 0;
    CREATE UNIQUE NONCLUSTERED INDEX UX_PipelineOutbox_MessageId ON meta.PipelineOutbox(MessageId);
    CREATE UNIQUE NONCLUSTERED INDEX UX_PipelineOutbox_PublicId ON meta.PipelineOutbox(PublicId);
END
GO
