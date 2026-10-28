-- Snapshot field: SnapshotSourceFid is the Fid of the field whose value is captured once, when a
-- record is created. NULL = not a snapshot field. SnapshotEditable allows manual edits afterwards.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'SnapshotSourceFid')
BEGIN
    ALTER TABLE meta.AppField ADD SnapshotSourceFid INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'SnapshotEditable')
BEGIN
    ALTER TABLE meta.AppField ADD SnapshotEditable BIT NOT NULL CONSTRAINT DF_AppField_SnapshotEditable DEFAULT 0;
END
GO
