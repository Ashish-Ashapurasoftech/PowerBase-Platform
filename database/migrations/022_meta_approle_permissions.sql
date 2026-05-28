-- Create AppRolePermission table
CREATE TABLE meta.AppRolePermission (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    AppRoleId BIGINT NOT NULL,
    PermissionId BIGINT NOT NULL,
    CONSTRAINT FK_AppRolePermission_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id),
    CONSTRAINT FK_AppRolePermission_Permission FOREIGN KEY (PermissionId) REFERENCES meta.Permission(Id)
);
GO

-- Migrate existing boolean flags to AppRolePermission
-- records:create = 10
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT Id, 10 FROM meta.AppRole WHERE CanAddRecords = 1;

-- records:read = 11
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT Id, 11 FROM meta.AppRole WHERE CanViewRecords = 1;

-- records:update = 12
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT Id, 12 FROM meta.AppRole WHERE CanEditRecords = 1;

-- records:delete = 13
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT Id, 13 FROM meta.AppRole WHERE CanDeleteRecords = 1;
GO

-- Also assign all other app-level permissions to the default 'Administrator' roles
-- Administrator roles have IsSystem = 1 and Name = 'Administrator'
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE r.IsSystem = 1 AND r.Name = 'Administrator'
  AND (p.Code LIKE 'tables:%' OR p.Code LIKE 'fields:%' OR p.Code LIKE 'reports:%')
  -- exclude ones we already added
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp 
      WHERE arp.AppRoleId = r.Id AND arp.PermissionId = p.Id
  );
GO

-- Assign read-only app-level permissions to the default 'Viewer' roles
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE r.IsSystem = 1 AND r.Name = 'Viewer'
  AND (p.Code = 'tables:read' OR p.Code = 'fields:read' OR p.Code = 'reports:read' OR p.Code = 'reports:run')
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp 
      WHERE arp.AppRoleId = r.Id AND arp.PermissionId = p.Id
  );
GO

-- Drop default constraints before dropping the columns
DECLARE @ConstraintName nvarchar(200);

SELECT @ConstraintName = Name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('meta.AppRole') AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'CanViewRecords');
IF @ConstraintName IS NOT NULL EXEC('ALTER TABLE meta.AppRole DROP CONSTRAINT ' + @ConstraintName);

SELECT @ConstraintName = Name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('meta.AppRole') AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'CanAddRecords');
IF @ConstraintName IS NOT NULL EXEC('ALTER TABLE meta.AppRole DROP CONSTRAINT ' + @ConstraintName);

SELECT @ConstraintName = Name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('meta.AppRole') AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'CanEditRecords');
IF @ConstraintName IS NOT NULL EXEC('ALTER TABLE meta.AppRole DROP CONSTRAINT ' + @ConstraintName);

SELECT @ConstraintName = Name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('meta.AppRole') AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'CanDeleteRecords');
IF @ConstraintName IS NOT NULL EXEC('ALTER TABLE meta.AppRole DROP CONSTRAINT ' + @ConstraintName);
GO

-- Remove boolean columns from AppRole
ALTER TABLE meta.AppRole
DROP COLUMN CanViewRecords, CanAddRecords, CanEditRecords, CanDeleteRecords;
GO

-- Remove app-level permissions from Tenant roles (RolePermission)
-- so they no longer appear in the Account Settings page
DELETE FROM meta.RolePermission 
WHERE PermissionId IN (
    SELECT Id FROM meta.Permission 
    WHERE Code LIKE 'tables:%' OR Code LIKE 'fields:%' OR Code LIKE 'records:%' OR Code LIKE 'reports:%'
);
GO
