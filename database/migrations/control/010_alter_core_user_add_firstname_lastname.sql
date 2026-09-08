IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'FirstName' AND Object_ID = Object_ID(N'core.[User]'))
BEGIN
    ALTER TABLE core.[User] ADD FirstName NVARCHAR(100) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'LastName' AND Object_ID = Object_ID(N'core.[User]'))
BEGIN
    ALTER TABLE core.[User] ADD LastName NVARCHAR(100) NULL;
END
GO

-- Backfill existing users: split Name into FirstName and LastName
UPDATE core.[User]
SET FirstName = CASE 
        WHEN CHARINDEX(' ', LTRIM(RTRIM(ISNULL(Name, '')))) > 0 
        THEN LEFT(LTRIM(RTRIM(Name)), CHARINDEX(' ', LTRIM(RTRIM(Name))) - 1)
        ELSE LTRIM(RTRIM(ISNULL(Name, '')))
    END,
    LastName = CASE 
        WHEN CHARINDEX(' ', LTRIM(RTRIM(ISNULL(Name, '')))) > 0 
        THEN LTRIM(SUBSTRING(LTRIM(RTRIM(Name)), CHARINDEX(' ', LTRIM(RTRIM(Name))) + 1, LEN(Name)))
        ELSE ''
    END
WHERE FirstName IS NULL;
GO
