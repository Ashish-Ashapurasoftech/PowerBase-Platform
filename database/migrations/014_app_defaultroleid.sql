-- Add DefaultAppRoleId to meta.App so each app can have a default role for new members.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.App') AND name = 'DefaultAppRoleId')
BEGIN
    ALTER TABLE meta.App ADD DefaultAppRoleId BIGINT NULL;
    ALTER TABLE meta.App ADD CONSTRAINT FK_App_DefaultAppRole
        FOREIGN KEY (DefaultAppRoleId) REFERENCES meta.AppRole(Id);
END
GO

-- Back-fill: set Viewer as the default role for existing apps.
UPDATE meta.App
SET DefaultAppRoleId = (
    SELECT TOP 1 ar.Id
    FROM meta.AppRole ar
    WHERE ar.AppId = meta.App.Id AND ar.Name = 'Viewer' AND ar.IsDeleted = 0
)
WHERE IsDeleted = 0 AND DefaultAppRoleId IS NULL;
GO
