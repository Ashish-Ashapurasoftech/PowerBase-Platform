-- Durable record of "this record (or this whole table) changed, stored c_{fid} columns must catch up".
-- Same pattern as meta.SearchOutbox (086): a row is written in the SAME transaction as the data change, so a change can never be
-- saved without its row. The compute worker drains the rows; a relay re-publishes rows that are still here after a grace period.
-- Rows hold ids only (no field values), so nothing sensitive is stored here. Additive and safe to re-run.
--
-- RecordId / RecordPublicId: exactly one is set for a single-record change (the write path knows one or the other; the worker resolves
-- the other). Both NULL = table-level work (import, copy, bulk type change, ...): "recompute this table".
-- ChangedFids: JSON array of the fids the change touched, e.g. [105,106]. NULL = unknown / all.
-- OldRefs: JSON object {"<referenceFid>": <old parent record Id or null>} for Reference fields whose value changed, so the OLD
--          parent's Summary can be recomputed too. NULL when no Reference field changed.
-- Reason: 0 create, 1 update, 2 delete, 3 mass update, 4 relink, 5 table-wide.

IF OBJECT_ID('meta.ComputeOutbox') IS NULL
BEGIN
    CREATE TABLE meta.ComputeOutbox (
        Id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ComputeOutbox PRIMARY KEY,
        TableId         BIGINT           NOT NULL,
        RecordId        BIGINT           NULL,
        RecordPublicId  UNIQUEIDENTIFIER NULL,
        Reason          TINYINT          NOT NULL,
        ChangedFids     NVARCHAR(MAX)    NULL,
        OldRefs         NVARCHAR(MAX)    NULL,
        CreatedOn       DATETIME2(3)     NOT NULL CONSTRAINT DF_ComputeOutbox_CreatedOn DEFAULT SYSUTCDATETIME(),
        Attempts        INT              NOT NULL CONSTRAINT DF_ComputeOutbox_Attempts DEFAULT 0,
        LastAttemptOn   DATETIME2(3)     NULL,
        LastError       NVARCHAR(400)    NULL
    );
END
GO

-- "does this table have pending work" (computePending) and the worker's per-table drain
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ComputeOutbox_Table' AND object_id = OBJECT_ID('meta.ComputeOutbox'))
    CREATE INDEX IX_ComputeOutbox_Table ON meta.ComputeOutbox (TableId, Id);
GO

-- what the relay scans: oldest rows first, still under the attempt limit
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ComputeOutbox_Due' AND object_id = OBJECT_ID('meta.ComputeOutbox'))
    CREATE INDEX IX_ComputeOutbox_Due ON meta.ComputeOutbox (Attempts, CreatedOn) INCLUDE (TableId);
GO
