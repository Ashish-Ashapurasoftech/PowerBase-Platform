-- QuickBase -> PowerBase migration: final sync passes (qbm schema only; see 001_qbm_state.sql).
-- Applied by the worker's "init" command to the tenant being migrated. Safe to run more than once.

-- One sync pass of one table: records changed in QuickBase since SinceUtc were copied again, records deleted in
-- QuickBase were marked deleted in PowerBase. The next pass starts from the StartedOn of the last finished one.
IF OBJECT_ID('qbm.Sync') IS NULL
BEGIN
    CREATE TABLE qbm.Sync (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmSync PRIMARY KEY,
        RunId           BIGINT        NOT NULL,
        TableMapId      BIGINT        NOT NULL,
        SinceUtc        DATETIME2(3)  NOT NULL,                 -- changes on or after this moment were looked for
        DryRun          BIT           NOT NULL CONSTRAINT DF_QbmSync_DryRun DEFAULT 0,
        StartedOn       DATETIME2(3)  NOT NULL CONSTRAINT DF_QbmSync_StartedOn DEFAULT SYSUTCDATETIME(),
        FinishedOn      DATETIME2(3)  NULL,
        Changed         INT           NULL,                     -- records copied again (changed in QuickBase)
        Deleted         INT           NULL,                     -- records marked deleted (gone from QuickBase); NULL = not checked
        LastError       NVARCHAR(2000) NULL
    );
    CREATE INDEX IX_QbmSync_Table ON qbm.Sync (TableMapId, FinishedOn);
END
GO
