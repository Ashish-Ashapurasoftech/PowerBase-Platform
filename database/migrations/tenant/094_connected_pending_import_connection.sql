-- QBL import: a connected table whose source application (or source table) was not available yet — neither already in
-- the tenant nor part of the same import batch. Kept here so the relationship can be completed automatically once that
-- application is imported, instead of being silently lost. Part of the Connected Tables module (see 083_connected_schema.sql);
-- no existing table is altered.
IF OBJECT_ID('connected.PendingImportConnection') IS NULL
BEGIN
    CREATE TABLE connected.PendingImportConnection (
        Id                     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConnectedPendingImport PRIMARY KEY,
        AppId                  BIGINT NOT NULL,                         -- the app the destination table belongs to
        AppTableId             BIGINT NOT NULL,                         -- the destination table, imported as an ordinary table
        SourceAppName          NVARCHAR(200) NOT NULL,                  -- derived from the QBL file's data source reference
        SourceTableName        NVARCHAR(200) NOT NULL,
        PayloadJson            NVARCHAR(MAX) NOT NULL,                  -- the QBL PblConnectedTable: field map, update mode, schedule, filter
        Status                 NVARCHAR(16) NOT NULL CONSTRAINT DF_ConnectedPendingImport_Status DEFAULT 'pending', -- pending | resolved
        Reason                 NVARCHAR(500) NULL,                      -- why it is still pending (not found / ambiguous / source table missing)
        AttemptCount           INT NOT NULL CONSTRAINT DF_ConnectedPendingImport_AttemptCount DEFAULT 0,
        CreatedOn              DATETIME2(3) NOT NULL CONSTRAINT DF_ConnectedPendingImport_CreatedOn DEFAULT SYSUTCDATETIME(),
        LastAttemptOn          DATETIME2(3) NULL,
        ResolvedOn             DATETIME2(3) NULL,
        ConnectedTablePublicId UNIQUEIDENTIFIER NULL,                   -- set once resolved, for traceability
        CONSTRAINT CK_ConnectedPendingImport_Status CHECK (Status IN ('pending', 'resolved'))
    );
    -- One open attempt per destination table: re-running the applier on the same table updates the existing row
    -- instead of piling up duplicates.
    CREATE UNIQUE INDEX UX_ConnectedPendingImport_Table ON connected.PendingImportConnection (AppTableId) WHERE Status = 'pending';
    -- The deferred resolver's lookup: "does any pending row wait on an app named like the one just imported?"
    CREATE INDEX IX_ConnectedPendingImport_SourceAppName ON connected.PendingImportConnection (SourceAppName) WHERE Status = 'pending';
END
GO
