IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'Preferences' AND Object_ID = Object_ID(N'core.[User]'))
BEGIN
    ALTER TABLE core.[User] ADD Preferences NVARCHAR(MAX) NULL;
END
GO
