-- Migration 101: audit.ActivityLog.RecordNumber — the Record ID# of the record a "Record" audit row is about, so the
-- Application Audit page can show it ("Record modified in Clients (Record ID# 125)") and find a record's history by that number.
-- Rows written before this migration keep RecordNumber NULL.
IF COL_LENGTH('audit.ActivityLog', 'RecordNumber') IS NULL
    ALTER TABLE audit.ActivityLog ADD RecordNumber BIGINT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ActivityLog_Table_RecordNumber' AND object_id = OBJECT_ID('audit.ActivityLog'))
    CREATE NONCLUSTERED INDEX IX_ActivityLog_Table_RecordNumber
        ON audit.ActivityLog (TableId, RecordNumber) WHERE RecordNumber IS NOT NULL;
GO
