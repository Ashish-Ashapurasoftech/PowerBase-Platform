IF NOT EXISTS (SELECT 1 FROM core.SystemRole WHERE Code = 'SuperAdmin')
    INSERT INTO core.SystemRole (Code, DisplayName, Description)
    VALUES ('SuperAdmin', 'Super Administrator', 'Full platform-level access. Assigned to internal Powerbase staff only.');

IF NOT EXISTS (SELECT 1 FROM core.SystemRole WHERE Code = 'User')
    INSERT INTO core.SystemRole (Code, DisplayName, Description)
    VALUES ('User', 'User', 'Standard platform user. Access governed by tenant roles.');
GO
