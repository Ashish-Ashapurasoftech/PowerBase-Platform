-- Tenant DB: seed PowerFlows copy permission.
-- Seed copy permission catalog entry
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'PowerFlows:copy')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('PowerFlows:copy', 'Copy PowerFlows', 'Allows duplicating/copying existing workflow automation PowerFlows');
END
GO

-- Bind PowerFlows:copy permission to the default 'Administrator' app roles
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE r.IsSystem = 1 AND r.Name = 'Administrator'
  AND p.Code = 'PowerFlows:copy'
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp 
      WHERE arp.AppRoleId = r.Id AND arp.PermissionId = p.Id
  );
GO
