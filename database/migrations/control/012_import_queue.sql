-- Dispatch queue for Import runs. The run itself (counters, log) lives in the tenant DB
-- (meta.ImportRun); this table only lets the worker find and lease queued runs across tenants.
IF OBJECT_ID('meta.ImportQueue') IS NULL
BEGIN
    CREATE TABLE meta.ImportQueue (
        Id             BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportQueue PRIMARY KEY,
        TenantId       BIGINT NOT NULL,
        RunPublicId    UNIQUEIDENTIFIER NOT NULL,
        Status         NVARCHAR(12) NOT NULL CONSTRAINT DF_ImportQueue_Status DEFAULT 'Pending', -- Pending | Processing | Done | Failed
        ClaimToken     UNIQUEIDENTIFIER NULL,
        LockedBy       NVARCHAR(100) NULL,
        LockedUntil    DATETIME2(3) NULL,
        AttemptCount   INT NOT NULL CONSTRAINT DF_ImportQueue_Attempts DEFAULT 0,
        NextAttemptOn  DATETIME2(3) NULL,
        LastError      NVARCHAR(1000) NULL,
        CreatedOn      DATETIME2(3) NOT NULL CONSTRAINT DF_ImportQueue_CreatedOn DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ImportQueue_Status CHECK (Status IN ('Pending', 'Processing', 'Done', 'Failed'))
    );
    CREATE UNIQUE INDEX UX_ImportQueue_Run ON meta.ImportQueue (RunPublicId);
    CREATE INDEX IX_ImportQueue_Claim ON meta.ImportQueue (Status, NextAttemptOn, CreatedOn) INCLUDE (TenantId, LockedUntil);
END
GO
