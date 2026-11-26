-- Durable dispatch queue for background APP imports (files over 3 MB; the job itself, file included, lives in the tenant DB:
-- meta.AppImportJob). Any API instance claims a queued job with a lease and renews it while the import runs, so a job queued on
-- one instance is run by whichever instance is free, and queued work survives restarts, deploys and scale-in.
-- IdentityJson: who uploaded it (user id, name, e-mail, permission codes, tenant role) — the import runs with exactly those rights.
-- No secrets. Additive and safe to re-run.
IF OBJECT_ID('meta.AppImportQueue') IS NULL
BEGIN
    CREATE TABLE meta.AppImportQueue (
        Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppImportQueue PRIMARY KEY,
        TenantId      BIGINT NOT NULL,
        JobPublicId   UNIQUEIDENTIFIER NOT NULL,
        IdentityJson  NVARCHAR(MAX) NOT NULL,
        Status        NVARCHAR(12) NOT NULL CONSTRAINT DF_AppImportQueue_Status DEFAULT 'Pending', -- Pending | Processing | Done
        ClaimToken    UNIQUEIDENTIFIER NULL,
        LockedBy      NVARCHAR(100) NULL,
        LockedUntil   DATETIME2(3) NULL,
        AttemptCount  INT NOT NULL CONSTRAINT DF_AppImportQueue_Attempts DEFAULT 0,
        CreatedOn     DATETIME2(3) NOT NULL CONSTRAINT DF_AppImportQueue_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_AppImportQueue_Status CHECK (Status IN ('Pending', 'Processing', 'Done'))
    );
    CREATE UNIQUE INDEX UX_AppImportQueue_Job ON meta.AppImportQueue (JobPublicId);
    CREATE INDEX IX_AppImportQueue_Claim ON meta.AppImportQueue (Status, CreatedOn) INCLUDE (TenantId, LockedUntil);
END
GO
