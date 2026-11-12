-- QuickBase -> PowerBase migration state (standalone module, see docs/qb-migration).
-- NOT part of database/migrations: the migration worker applies this only to the tenant being migrated
-- ("init" command), so no other tenant and no CI migration run ever gets it. Everything lives in its own
-- 'qbm' schema; no existing table is altered. Safe to run more than once.
IF SCHEMA_ID('qbm') IS NULL EXEC('CREATE SCHEMA qbm');
GO

-- One migration of one QuickBase app into one PowerBase app.
IF OBJECT_ID('qbm.Run') IS NULL
BEGIN
    CREATE TABLE qbm.Run (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmRun PRIMARY KEY,
        QbRealm         NVARCHAR(200) NOT NULL,
        QbAppId         NVARCHAR(32)  NOT NULL,
        PbAppId         BIGINT        NOT NULL,
        RunAsUserId     BIGINT        NOT NULL,                 -- owner for system fields when a QB user can't be mapped
        Status          NVARCHAR(16)  NOT NULL CONSTRAINT DF_QbmRun_Status DEFAULT 'planned', -- planned | loading | loaded | validated | failed
        Note            NVARCHAR(1000) NULL,
        CreatedOn       DATETIME2(3)  NOT NULL CONSTRAINT DF_QbmRun_CreatedOn DEFAULT SYSUTCDATETIME(),
        ModifiedOn      DATETIME2(3)  NULL
    );
END
GO

-- QuickBase table -> PowerBase table, with the counts the planner saw.
IF OBJECT_ID('qbm.TableMap') IS NULL
BEGIN
    CREATE TABLE qbm.TableMap (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmTableMap PRIMARY KEY,
        RunId           BIGINT        NOT NULL,
        QbTableId       NVARCHAR(32)  NOT NULL,
        QbTableName     NVARCHAR(400) NOT NULL,
        PbTableId       BIGINT        NOT NULL,
        Tier            INT           NOT NULL CONSTRAINT DF_QbmTableMap_Tier DEFAULT 0,
        QbRecordCount   BIGINT        NULL,
        QbMinRecordId   BIGINT        NULL,
        QbMaxRecordId   BIGINT        NULL,
        Status          NVARCHAR(16)  NOT NULL CONSTRAINT DF_QbmTableMap_Status DEFAULT 'mapped', -- mapped | blocked | skipped | planned | loading | loaded | validated
        CONSTRAINT UX_QbmTableMap UNIQUE (RunId, QbTableId)
    );
END
GO

-- QuickBase field -> PowerBase field. Action says what the loader does with it.
IF OBJECT_ID('qbm.FieldMap') IS NULL
BEGIN
    CREATE TABLE qbm.FieldMap (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmFieldMap PRIMARY KEY,
        TableMapId      BIGINT        NOT NULL,
        QbFid           INT           NOT NULL,
        QbLabel         NVARCHAR(400) NOT NULL,
        QbType          NVARCHAR(50)  NOT NULL,
        PbFid           INT           NULL,
        PbTypeCode      NVARCHAR(50)  NULL,
        Action          NVARCHAR(16)  NOT NULL,                 -- load | system | address-part | computed | skip | block
        Reason          NVARCHAR(500) NULL,
        CONSTRAINT UX_QbmFieldMap UNIQUE (TableMapId, QbFid)
    );
END
GO

-- The chunk ledger: one Record ID range of one table. A chunk is only Done after its rows committed.
IF OBJECT_ID('qbm.Chunk') IS NULL
BEGIN
    CREATE TABLE qbm.Chunk (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmChunk PRIMARY KEY,
        RunId           BIGINT        NOT NULL,
        TableMapId      BIGINT        NOT NULL,
        FromRecordId    BIGINT        NOT NULL,                 -- inclusive
        ToRecordId      BIGINT        NOT NULL,                 -- exclusive
        Status          NVARCHAR(12)  NOT NULL CONSTRAINT DF_QbmChunk_Status DEFAULT 'pending', -- pending | running | done | failed
        Attempts        INT           NOT NULL CONSTRAINT DF_QbmChunk_Attempts DEFAULT 0,
        RowsRead        INT           NULL,
        RowsWritten     INT           NULL,
        Checksum        VARBINARY(32) NULL,                     -- SHA-256 over the transformed rows, in Record ID order
        LastError       NVARCHAR(2000) NULL,
        StartedOn       DATETIME2(3)  NULL,
        FinishedOn      DATETIME2(3)  NULL,
        CONSTRAINT UX_QbmChunk_Range UNIQUE (TableMapId, FromRecordId)
    );
    CREATE INDEX IX_QbmChunk_Run_Status ON qbm.Chunk (RunId, Status);
END
GO

-- Anything a person should look at: unmapped values, skipped fields, gate failures.
IF OBJECT_ID('qbm.Issue') IS NULL
BEGIN
    CREATE TABLE qbm.Issue (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QbmIssue PRIMARY KEY,
        RunId           BIGINT        NOT NULL,
        TableMapId      BIGINT        NULL,
        ChunkId         BIGINT        NULL,
        QbRecordId      BIGINT        NULL,
        QbFid           INT           NULL,
        Severity        NVARCHAR(8)   NOT NULL,                 -- info | warning | error
        Code            NVARCHAR(60)  NOT NULL,
        Message         NVARCHAR(2000) NOT NULL,
        CreatedOn       DATETIME2(3)  NOT NULL CONSTRAINT DF_QbmIssue_CreatedOn DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_QbmIssue_Run ON qbm.Issue (RunId, Severity);
END
GO
