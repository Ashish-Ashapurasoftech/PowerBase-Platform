-- Import history: every run now keeps a details file per destination table (all outcomes, with the Record ID#), the uploaded source file is kept
-- for a retention period, and a merge counts the records it left alone because nothing changed.
IF COL_LENGTH('meta.ImportRun', 'Unchanged') IS NULL
    ALTER TABLE meta.ImportRun ADD Unchanged BIGINT NOT NULL CONSTRAINT DF_ImportRun_Unchanged DEFAULT 0;
GO
-- Set when the run's files (details files, uploaded source file) have been deleted at the end of their retention.
IF COL_LENGTH('meta.ImportRun', 'FilesExpiredOn') IS NULL
    ALTER TABLE meta.ImportRun ADD FilesExpiredOn DATETIME2(3) NULL;
GO
IF COL_LENGTH('meta.ImportRunTarget', 'Unchanged') IS NULL
    ALTER TABLE meta.ImportRunTarget ADD Unchanged BIGINT NOT NULL CONSTRAINT DF_ImportRunTarget_Unchanged DEFAULT 0;
GO
-- Storage path of this table's details file (internal: downloads go through an authorised endpoint).
IF COL_LENGTH('meta.ImportRunTarget', 'DetailsFilePath') IS NULL
    ALTER TABLE meta.ImportRunTarget ADD DetailsFilePath NVARCHAR(500) NULL;
GO
-- A source file a run has used is kept until this time instead of being deleted when the run ends.
IF COL_LENGTH('meta.ImportFile', 'RetainedUntil') IS NULL
    ALTER TABLE meta.ImportFile ADD RetainedUntil DATETIME2(3) NULL;
GO
-- The retention clean-up looks only at runs whose files are still there.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ImportRun_FilesExpiry' AND object_id = OBJECT_ID('meta.ImportRun'))
    CREATE INDEX IX_ImportRun_FilesExpiry ON meta.ImportRun (CompletedOn) WHERE FilesExpiredOn IS NULL AND CompletedOn IS NOT NULL;
GO
-- The history screen reads the last 30 days of runs, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ImportRun_CreatedOn' AND object_id = OBJECT_ID('meta.ImportRun'))
    CREATE INDEX IX_ImportRun_CreatedOn ON meta.ImportRun (CreatedOn DESC) INCLUDE (ImportDefinitionId, Status, CompletedOn);
GO
