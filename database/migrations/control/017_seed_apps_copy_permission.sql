-- Control plane: tenant-level permission apps:copy ("Copy Apps"), checked by Copy App instead of apps:create.
-- Tenant-level permissions live here, next to apps:create: meta.Permission (catalog) and meta.RolePermission (per
-- tenant role). The app-level rule stays in the CopyApp command: app Administrator, or "Allow non-administrators to copy".
--
-- Backfill: every tenant role that can create apps today (all Administrator roles, and custom roles given apps:create)
-- gets apps:copy, so nobody loses Copy App. New tenants' Administrator role gets it with all the other permissions.
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'apps:copy')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES ('apps:copy', 'Copy Apps', 'Copy an application (its structure, and its data for app administrators)');
END
GO

INSERT INTO meta.RolePermission (TenantRoleId, PermissionId)
SELECT tr.Id, copyPermission.Id
FROM meta.TenantRole tr
CROSS JOIN (SELECT Id FROM meta.Permission WHERE Code = 'apps:copy') copyPermission
WHERE (
        (tr.IsSystem = 1 AND tr.Name = 'Administrator')
        OR EXISTS (
            SELECT 1 FROM meta.RolePermission rp
            JOIN meta.Permission createPermission ON createPermission.Id = rp.PermissionId
            WHERE rp.TenantRoleId = tr.Id AND createPermission.Code = 'apps:create')
      )
  AND NOT EXISTS (
    SELECT 1 FROM meta.RolePermission existing
    WHERE existing.TenantRoleId = tr.Id AND existing.PermissionId = copyPermission.Id
);
GO
