IF NOT EXISTS (SELECT 1 FROM core.SystemRole WHERE Code = 'SuperAdmin')
    INSERT INTO core.SystemRole (Code, Name) VALUES ('SuperAdmin', 'Super Administrator');

IF NOT EXISTS (SELECT 1 FROM core.SystemRole WHERE Code = 'User')
    INSERT INTO core.SystemRole (Code, Name) VALUES ('User', 'User');
GO
