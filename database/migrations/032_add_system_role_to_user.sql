-- Add SystemRoleId to core.[User] to support SuperAdmin designation.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('core.[User]') AND name = 'SystemRoleId')
    ALTER TABLE core.[User] ADD SystemRoleId INT NULL
        CONSTRAINT FK_User_SystemRole FOREIGN KEY REFERENCES core.SystemRole(Id);
GO
