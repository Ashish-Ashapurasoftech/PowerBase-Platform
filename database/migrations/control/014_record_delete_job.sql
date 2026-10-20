-- Background jobs for "Delete these records" on a Table report. The job row records the confirmed filter
-- snapshot and live progress; a worker (RecordDeleteJobWorker) finds Pending / InProgress jobs across tenants,
-- deletes the matching records in small chunks and finally marks the job Completed.
IF OBJECT_ID('meta.RecordDeleteJob') IS NULL
BEGIN
    CREATE TABLE meta.RecordDeleteJob (
        Id                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RecordDeleteJob PRIMARY KEY,
        PublicId          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_RecordDeleteJob_PublicId DEFAULT NEWID(),
        TenantId          BIGINT NOT NULL,
        ReportPublicId    UNIQUEIDENTIFIER NOT NULL,
        TablePublicId     UNIQUEIDENTIFIER NOT NULL,
        TableName         NVARCHAR(200) NULL,
        RequestedByUserId BIGINT NOT NULL,
        IsSuperAdmin      BIT NOT NULL CONSTRAINT DF_RecordDeleteJob_SuperAdmin DEFAULT 0,
        TenantRole        NVARCHAR(100) NOT NULL CONSTRAINT DF_RecordDeleteJob_TenantRole DEFAULT '',
        FilterJson        NVARCHAR(MAX) NOT NULL,   -- the exact filters the user saw (RecordDeleteJobFilter)
        ExpectedCount     INT NOT NULL,             -- the count the user confirmed; the job never deletes more
        DeletedCount      INT NOT NULL CONSTRAINT DF_RecordDeleteJob_Deleted DEFAULT 0,
        Status            NVARCHAR(12) NOT NULL CONSTRAINT DF_RecordDeleteJob_Status DEFAULT 'Pending', -- Pending | InProgress | Completed | Failed | Cancelled
        CancelRequested   BIT NOT NULL CONSTRAINT DF_RecordDeleteJob_Cancel DEFAULT 0,
        ClaimToken        UNIQUEIDENTIFIER NULL,
        LockedBy          NVARCHAR(100) NULL,
        LockedUntil       DATETIME2(3) NULL,
        LastError         NVARCHAR(1000) NULL,
        CreatedOn         DATETIME2(3) NOT NULL CONSTRAINT DF_RecordDeleteJob_CreatedOn DEFAULT SYSUTCDATETIME(),
        StartedOn         DATETIME2(3) NULL,
        CompletedOn       DATETIME2(3) NULL,
        CONSTRAINT CK_RecordDeleteJob_Status CHECK (Status IN ('Pending', 'InProgress', 'Completed', 'Failed', 'Cancelled'))
    );
    CREATE UNIQUE INDEX UX_RecordDeleteJob_PublicId ON meta.RecordDeleteJob (PublicId);
    CREATE INDEX IX_RecordDeleteJob_Claim ON meta.RecordDeleteJob (Status, CreatedOn) INCLUDE (LockedUntil, TenantId);
    CREATE INDEX IX_RecordDeleteJob_Table ON meta.RecordDeleteJob (TenantId, TablePublicId, Status);
END
GO
