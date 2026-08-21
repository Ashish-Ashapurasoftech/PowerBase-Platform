-- Tenant DB: seed new PowerFlows permissions.
-- Seed new permission catalog entries
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'PowerFlows:create')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('PowerFlows:create', 'Create PowerFlows', 'Allows creating new workflow automation PowerFlows'),
        ('PowerFlows:read',   'View PowerFlows',   'Allows viewing the list and execution runs of PowerFlows'),
        ('PowerFlows:update', 'Update PowerFlows', 'Allows modifying PowerFlow layouts and step settings'),
        ('PowerFlows:delete', 'Delete PowerFlows', 'Allows deleting PowerFlows');
END
GO

-- Bind new PowerFlows permissions to the default 'Administrator' app roles
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE r.IsSystem = 1 AND r.Name = 'Administrator'
  AND p.Code LIKE 'PowerFlows:%'
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp 
      WHERE arp.AppRoleId = r.Id AND arp.PermissionId = p.Id
  );
GO

-- Bind view-only PowerFlows permissions to default 'Viewer' app roles
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE r.IsSystem = 1 AND r.Name = 'Viewer'
  AND p.Code = 'PowerFlows:read'
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp 
      WHERE arp.AppRoleId = r.Id AND arp.PermissionId = p.Id
  );
GO
