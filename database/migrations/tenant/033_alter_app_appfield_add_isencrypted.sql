IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.App') AND name = 'IsEncrypted')
BEGIN
    ALTER TABLE meta.App ADD IsEncrypted BIT NOT NULL DEFAULT 0;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'IsEncrypted')
BEGIN
    ALTER TABLE meta.AppField ADD IsEncrypted BIT NOT NULL DEFAULT 0;
END
GO
