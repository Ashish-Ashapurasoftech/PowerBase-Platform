IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('meta.Tenant') AND name = 'SupportsSecureEnclaves')
BEGIN
    ALTER TABLE meta.Tenant ADD SupportsSecureEnclaves BIT NOT NULL DEFAULT 0;
END
GO
