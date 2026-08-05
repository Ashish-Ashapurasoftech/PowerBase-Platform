IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'Branding' AND Object_ID = Object_ID(N'meta.App'))
BEGIN
    ALTER TABLE meta.App ADD Branding NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'LayoutSettings' AND Object_ID = Object_ID(N'meta.App'))
BEGIN
    ALTER TABLE meta.App ADD LayoutSettings NVARCHAR(MAX) NULL;
END
GO
