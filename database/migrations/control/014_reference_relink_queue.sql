-- Dispatch queue for re-linking child records to a parent record that was added (or whose key changed) after the children
-- were written: their reference is blank and the text that matched no parent is kept in the hidden "(original)" field.
-- The parent's save queues a job after its commit; ReferenceRelinkWorker leases jobs across tenants and links the children.
-- A job carries record IDs only, never key values: the worker reads each parent's current key from the tenant DB.
-- A job that succeeds is deleted; Failed and Skipped jobs stay, with the reason in LastError.
IF OBJECT_ID('meta.ReferenceRelinkQueue') IS NULL
BEGIN
    CREATE TABLE meta.ReferenceRelinkQueue (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReferenceRelinkQueue PRIMARY KEY,
        TenantId         BIGINT NOT NULL,
        ParentTableId    BIGINT NOT NULL,
        ParentRecordIds  NVARCHAR(MAX) NOT NULL,      -- JSON array of the parent records' Record IDs, e.g. [500]
        RequestedBy      BIGINT NOT NULL,             -- the user whose save queued the job (ModifiedBy / audit of the linked rows)
        Status           NVARCHAR(12) NOT NULL CONSTRAINT DF_ReferenceRelinkQueue_Status DEFAULT 'Pending', -- Pending | Processing | Failed | Skipped
        ClaimToken       UNIQUEIDENTIFIER NULL,
        LockedBy         NVARCHAR(100) NULL,
        LockedUntil      DATETIME2(3) NULL,
        AttemptCount     INT NOT NULL CONSTRAINT DF_ReferenceRelinkQueue_Attempts DEFAULT 0,
        NextAttemptOn    DATETIME2(3) NULL,
        LastError        NVARCHAR(1000) NULL,
        CreatedOn        DATETIME2(3) NOT NULL CONSTRAINT DF_ReferenceRelinkQueue_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ReferenceRelinkQueue_Status CHECK (Status IN ('Pending', 'Processing', 'Failed', 'Skipped'))
    );
    CREATE INDEX IX_ReferenceRelinkQueue_Claim ON meta.ReferenceRelinkQueue (Status, NextAttemptOn, CreatedOn) INCLUDE (LockedUntil);
END
GO
