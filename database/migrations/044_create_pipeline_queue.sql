-- Create meta.PipelineQueue table for centralized database-backed pipeline execution queue
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PipelineQueue')
BEGIN
    CREATE TABLE meta.PipelineQueue (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        MessageId UNIQUEIDENTIFIER NOT NULL,
        TenantId BIGINT NOT NULL,
        TenantPublicId UNIQUEIDENTIFIER NOT NULL,
        PipelineId BIGINT NOT NULL,
        PipelinePublicId UNIQUEIDENTIFIER NOT NULL,
        QueueSource NVARCHAR(20) NOT NULL, -- Event, Manual, Schedule, Webhook
        TriggerStepId BIGINT NULL,
        TriggerStepRefId NVARCHAR(100) NULL,
        TriggerEvent VARCHAR(50) NULL,
        TriggerPayloadJson NVARCHAR(MAX) NOT NULL,
        PayloadHash VARBINARY(32) NOT NULL, -- SHA-256 hash of payload bytes
        TriggeredBy BIGINT NULL,
        TriggerTablePublicId UNIQUEIDENTIFIER NULL,
        CorrelationId UNIQUEIDENTIFIER NULL,
        Depth INT NOT NULL DEFAULT 1,
        PipelineChain NVARCHAR(4000) NOT NULL DEFAULT '[]', -- Bounded chain
        BatchId UNIQUEIDENTIFIER NULL,
        VariablesJson NVARCHAR(MAX) NULL,
        PayloadVersion NVARCHAR(20) NOT NULL DEFAULT '1.0',
        EventTimestamp DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        
        -- Execution state
        Status NVARCHAR(20) NOT NULL DEFAULT 'Pending', -- 'Pending', 'Processing', 'Succeeded', 'Skipped', 'Failed'
        AttemptCount INT NOT NULL DEFAULT 0,
        MaxAttempts INT NOT NULL DEFAULT 5,
        NextAttemptOn DATETIME2(3) NULL,
        LockedBy VARCHAR(100) NULL,
        LockedUntil DATETIME2(3) NULL,
        ClaimToken UNIQUEIDENTIFIER NULL, -- Distinct claim identifier
        
        -- Audit Timestamps
        CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        StartedOn DATETIME2(3) NULL,
        CompletedOn DATETIME2(3) NULL,
        FailedOn DATETIME2(3) NULL,
        SkippedOn DATETIME2(3) NULL,
        LastModifiedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), -- Retained for C# codebase compatibility
        LastError NVARCHAR(2000) NULL,
        SkipReason NVARCHAR(2000) NULL,

        -- Constraints
        CONSTRAINT PK_PipelineQueue PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_PipelineQueue_PublicId UNIQUE (PublicId),
        CONSTRAINT UX_PipelineQueue_MessageId UNIQUE (MessageId),
        CONSTRAINT FK_PipelineQueue_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id),
        CONSTRAINT CHK_PipelineQueue_Status CHECK (Status IN ('Pending', 'Processing', 'Succeeded', 'Skipped', 'Failed')),
        CONSTRAINT CHK_PipelineQueue_AttemptCount CHECK (AttemptCount >= 0),
        CONSTRAINT CHK_PipelineQueue_MaxAttempts CHECK (MaxAttempts > 0),
        CONSTRAINT CHK_PipelineQueue_Depth CHECK (Depth >= 1 AND Depth <= 10),
        CONSTRAINT CHK_PipelineQueue_QueueSource CHECK (QueueSource IN ('Event', 'Manual', 'Schedule', 'Webhook'))
    );
END
GO

-- Indexing Strategy
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_Claim' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_Claim 
    ON meta.PipelineQueue (Status, NextAttemptOn)
    INCLUDE (LockedUntil, AttemptCount, MaxAttempts, ClaimToken)
    WHERE Status = 'Pending';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_Reclaim' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_Reclaim
    ON meta.PipelineQueue (Status, LockedUntil)
    INCLUDE (ClaimToken, AttemptCount, MaxAttempts)
    WHERE Status = 'Processing';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_Tenant_Status' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_Tenant_Status
    ON meta.PipelineQueue (TenantId, Status);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_Terminal_Cleanup' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_Terminal_Cleanup
    ON meta.PipelineQueue (Status)
    INCLUDE (CompletedOn, FailedOn, SkippedOn);
END
GO

