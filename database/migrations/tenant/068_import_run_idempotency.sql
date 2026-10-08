-- A caller of the run API may send a client token so that retrying a request (a dropped connection, a double click in a script)
-- starts one run, not two. The token is stored as a SHA-256 hex digest, never as sent, and is unique per import.
IF COL_LENGTH('meta.ImportRun', 'IdempotencyKey') IS NULL
    ALTER TABLE meta.ImportRun ADD IdempotencyKey CHAR(64) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_ImportRun_Idempotency' AND object_id = OBJECT_ID('meta.ImportRun'))
    CREATE UNIQUE INDEX UX_ImportRun_Idempotency ON meta.ImportRun (ImportDefinitionId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
GO
