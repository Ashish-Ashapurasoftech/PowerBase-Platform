-- Background jobs for "Reinitialize" of a Snapshot field: re-captures the field's source value for every existing
-- record of the table. The job row records who asked for it and live progress; a worker (FieldSnapshotJobWorker)
-- finds Pending / InProgress jobs across tenants, walks the table by record Id in small chunks and finally marks the
-- job Completed. LastRecordId is the resume cursor, so a restarted worker continues where the previous one stopped.
IF OBJECT_ID('meta.FieldSnapshotJob') IS NULL
BEGIN
    CREATE TABLE meta.FieldSnapshotJob (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldSnapshotJob PRIMARY KEY,
        PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_FieldSnapshotJob_PublicId DEFAULT NEWID(),
        TenantId          BIGINT NOT NULL,
        TablePublicId     UNIQUEIDENTIFIER NOT NULL,
        TableName         NVARCHAR(200) NULL,
        FieldPublicId     UNIQUEIDENTIFIER NOT NULL,
        FieldLabel        NVARCHAR(200) NULL,
        RequestedByUserId BIGINT NOT NULL,
        IsSuperAdmin      BIT NOT NULL CONSTRAINT DF_FieldSnapshotJob_SuperAdmin DEFAULT 0,
        TenantRole        NVARCHAR(100) NOT NULL CONSTRAINT DF_FieldSnapshotJob_TenantRole DEFAULT '',
        TotalCount        INT NOT NULL,             -- records in the table when the job was queued (for the progress bar)
        ProcessedCount    INT NOT NULL CONSTRAINT DF_FieldSnapshotJob_Processed DEFAULT 0,
        ChangedCount      INT NOT NULL CONSTRAINT DF_FieldSnapshotJob_Changed DEFAULT 0,   -- records whose value actually changed
        LastRecordId      BIGINT NOT NULL CONSTRAINT DF_FieldSnapshotJob_LastRecordId DEFAULT 0,
        Status            NVARCHAR(12) NOT NULL CONSTRAINT DF_FieldSnapshotJob_Status DEFAULT 'Pending', -- Pending | InProgress | Completed | Failed | Cancelled
        CancelRequested   BIT NOT NULL CONSTRAINT DF_FieldSnapshotJob_Cancel DEFAULT 0,
        ClaimToken        UNIQUEIDENTIFIER NULL,
        LockedBy          NVARCHAR(100) NULL,
        LockedUntil       DATETIME2(3) NULL,
        LastError         NVARCHAR(1000) NULL,
        CreatedOn         DATETIME2(3) NOT NULL CONSTRAINT DF_FieldSnapshotJob_CreatedOn DEFAULT SYSUTCDATETIME(),
        StartedOn         DATETIME2(3) NULL,
        CompletedOn       DATETIME2(3) NULL,
        CONSTRAINT CK_FieldSnapshotJob_Status CHECK (Status IN ('Pending', 'InProgress', 'Completed', 'Failed', 'Cancelled'))
    );
    CREATE UNIQUE INDEX UX_FieldSnapshotJob_PublicId ON meta.FieldSnapshotJob (PublicId);
    CREATE INDEX IX_FieldSnapshotJob_Claim ON meta.FieldSnapshotJob (Status, CreatedOn) INCLUDE (LockedUntil, TenantId);
    CREATE INDEX IX_FieldSnapshotJob_Field ON meta.FieldSnapshotJob (TenantId, FieldPublicId, Status);
END
GO
