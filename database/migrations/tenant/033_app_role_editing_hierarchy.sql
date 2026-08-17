-- Adds role editing hierarchy settings to meta.AppRole
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'ManageableRolesType')
BEGIN
    ALTER TABLE meta.AppRole ADD ManageableRolesType NVARCHAR(50) NOT NULL DEFAULT 'None';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'Rank')
BEGIN
    ALTER TABLE meta.AppRole ADD Rank INT NULL;
END
GO

-- Create meta.AppRoleManageableRole table for explicit manual select setting
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleManageableRole')
BEGIN
    CREATE TABLE meta.AppRoleManageableRole (
        Id BIGINT IDENTITY(1,1) NOT NULL,
        AppRoleId BIGINT NOT NULL,
        ManageableRoleId BIGINT NOT NULL,
        CONSTRAINT PK_AppRoleManageableRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRoleManageableRole UNIQUE (AppRoleId, ManageableRoleId),
        CONSTRAINT FK_AppRoleManageableRole_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleManageableRole_ManageableRole FOREIGN KEY (ManageableRoleId) REFERENCES meta.AppRole(Id)
    );
END
GO

-- Seed default ranks for system-defined roles in existing apps
-- Administrator = 1, Participant = 2, Viewer = 3
UPDATE meta.AppRole SET Rank = 1, ManageableRolesType = 'Below' WHERE Name = 'Administrator' AND IsSystem = 1 AND Rank IS NULL;
UPDATE meta.AppRole SET Rank = 2, ManageableRolesType = 'None' WHERE Name = 'Participant' AND IsSystem = 1 AND Rank IS NULL;
UPDATE meta.AppRole SET Rank = 3, ManageableRolesType = 'None' WHERE Name = 'Viewer' AND IsSystem = 1 AND Rank IS NULL;
GO
