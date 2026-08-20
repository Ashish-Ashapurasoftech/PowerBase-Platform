IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'App')
BEGIN
    ALTER TABLE meta.App ADD IsEncrypted BIT NOT NULL DEFAULT 0;
END
GO
IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppField')
BEGIN
    ALTER TABLE meta.AppField ADD IsEncrypted BIT NOT NULL DEFAULT 0;
END
GO
