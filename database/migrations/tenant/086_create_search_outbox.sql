-- Durable record of "this record changed, the search index must catch up".
-- A row is written in the SAME transaction as the record change, so a change can never be saved without its row. The indexer deletes
-- the row once the record is really in the index; a relay re-publishes any row that is still here after a grace period (the event was
-- lost, or the indexer failed), and a daily sweep reports and re-drives what is stuck.
-- Rows hold ids only (no field values), so nothing sensitive is stored here. Additive and safe to re-run.

IF OBJECT_ID('meta.SearchOutbox') IS NULL
BEGIN
    CREATE TABLE meta.SearchOutbox (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SearchOutbox PRIMARY KEY,
        TableId         BIGINT           NOT NULL,
        AppId           BIGINT           NOT NULL,
        RecordPublicId  UNIQUEIDENTIFIER NOT NULL,
        Action          TINYINT          NOT NULL,                                   -- 0 = upsert, 1 = delete
        Version         BIGINT           NOT NULL,                                   -- unix ms when the change was made; orders changes of one record
        CreatedOn       DATETIME2(3)     NOT NULL CONSTRAINT DF_SearchOutbox_CreatedOn DEFAULT SYSUTCDATETIME(),
        Attempts        INT              NOT NULL CONSTRAINT DF_SearchOutbox_Attempts DEFAULT 0,
        LastAttemptOn   DATETIME2(3)     NULL,
        LastError       NVARCHAR(400)    NULL
    );
END
GO

-- what the relay scans: oldest rows first, still under the attempt limit
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SearchOutbox_Due' AND object_id = OBJECT_ID('meta.SearchOutbox'))
    CREATE INDEX IX_SearchOutbox_Due ON meta.SearchOutbox (Attempts, CreatedOn) INCLUDE (TableId, AppId, RecordPublicId, Action, Version);
GO

-- what the indexer's acknowledgement looks up
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SearchOutbox_Record' AND object_id = OBJECT_ID('meta.SearchOutbox'))
    CREATE INDEX IX_SearchOutbox_Record ON meta.SearchOutbox (TableId, RecordPublicId) INCLUDE (Version, Action);
GO
