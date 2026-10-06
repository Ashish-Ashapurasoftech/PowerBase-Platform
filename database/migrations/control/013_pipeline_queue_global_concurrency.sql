-- Supports globally coordinated queue claims across multiple worker replicas.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_ActiveCapacity' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_ActiveCapacity
    ON meta.PipelineQueue (Status, TenantId, PipelineId, LockedUntil)
    INCLUDE (CreatedOn, AttemptCount, MaxAttempts)
    WHERE Status = 'Processing';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PipelineQueue_FairClaim' AND object_id = OBJECT_ID('meta.PipelineQueue'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PipelineQueue_FairClaim
    ON meta.PipelineQueue (Status, TenantId, PipelineId, CreatedOn, Id)
    INCLUDE (NextAttemptOn, AttemptCount, MaxAttempts)
    WHERE Status = 'Pending';
END
GO
