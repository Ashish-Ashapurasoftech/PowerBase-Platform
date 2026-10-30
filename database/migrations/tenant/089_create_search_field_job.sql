-- Durable record of "one field's change must still reach every record in the search index" (a field switched on or off for search, a
-- type change, a deleted field's column to empty). The row is written before the job's first message is sent and deleted when its
-- last page is indexed; AfterId is how far it has got. The search relay re-sends a job whose progress has stopped (its message was
-- lost, or dead-lettered), from where it stopped, and reports read from SQL instead of the index for a field whose job is still running.
-- Rows hold ids only (no field values). Additive and safe to re-run.

IF OBJECT_ID('meta.SearchFieldJob') IS NULL
BEGIN
    CREATE TABLE meta.SearchFieldJob (
        Id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SearchFieldJob PRIMARY KEY,
        TableId     BIGINT        NOT NULL,
        AppId       BIGINT        NOT NULL,
        Action      TINYINT       NOT NULL,                                          -- 2 = write the field's values, 3 = empty its column(s)
        FieldId     BIGINT        NOT NULL,                                          -- the field's Fid
        Slot        NVARCHAR(40)  NULL,                                              -- the column to empty (a deleted field, a type change)
        EndSlot     NVARCHAR(40)  NULL,
        AfterId     BIGINT        NOT NULL CONSTRAINT DF_SearchFieldJob_AfterId DEFAULT 0, -- records up to this id are done
        CreatedOn   DATETIME2(3)  NOT NULL CONSTRAINT DF_SearchFieldJob_CreatedOn DEFAULT SYSUTCDATETIME(),
        UpdatedOn   DATETIME2(3)  NOT NULL CONSTRAINT DF_SearchFieldJob_UpdatedOn DEFAULT SYSUTCDATETIME(),
        Attempts    INT           NOT NULL CONSTRAINT DF_SearchFieldJob_Attempts DEFAULT 0,
        LastError   NVARCHAR(400) NULL
    );
END
GO

-- what reports look up: the running jobs of one table
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SearchFieldJob_Table' AND object_id = OBJECT_ID('meta.SearchFieldJob'))
    CREATE INDEX IX_SearchFieldJob_Table ON meta.SearchFieldJob (TableId) INCLUDE (FieldId, Action, Slot, EndSlot, AfterId);
GO

-- what the relay scans: jobs whose progress stopped, under the attempt limit
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SearchFieldJob_Due' AND object_id = OBJECT_ID('meta.SearchFieldJob'))
    CREATE INDEX IX_SearchFieldJob_Due ON meta.SearchFieldJob (Attempts, UpdatedOn);
GO
