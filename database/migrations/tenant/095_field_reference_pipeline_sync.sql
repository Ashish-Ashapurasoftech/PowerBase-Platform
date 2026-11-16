-- Tenant DB: remembers how far the PowerFlow field-reference sync has read.
--
-- PowerFlows are saved by another module inside its own transaction, so the Usage index learns about edits by scanning for
-- PowerFlows (or steps) created / modified / deleted since this point (see IFieldReferenceIndexer.SyncPipelinesAsync).
-- One row only (Id = 1). Derived data: dropping it just makes the next sync re-index every PowerFlow once.

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FieldReferencePipelineSync')
BEGIN
    CREATE TABLE meta.FieldReferencePipelineSync (
        Id         INT          NOT NULL,
        SyncedUpTo DATETIME2(7) NOT NULL,
        CONSTRAINT PK_FieldReferencePipelineSync PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_FieldReferencePipelineSync_OneRow CHECK (Id = 1)
    );
END
GO
