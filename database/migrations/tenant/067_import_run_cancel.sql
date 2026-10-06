-- A user can ask for a running import to stop. The worker sees the flag when it records each chunk's progress.
IF COL_LENGTH('meta.ImportRun', 'CancelRequested') IS NULL
    ALTER TABLE meta.ImportRun ADD CancelRequested BIT NOT NULL CONSTRAINT DF_ImportRun_CancelRequested DEFAULT 0;
GO
