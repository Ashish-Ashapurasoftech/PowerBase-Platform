IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'Formatting' AND Object_ID = Object_ID(N'meta.App'))
BEGIN
    ALTER TABLE meta.App ADD Formatting NVARCHAR(MAX) NULL;
END
GO
