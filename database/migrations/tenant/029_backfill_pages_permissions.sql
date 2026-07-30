-- Backfill pages:* permissions onto existing built-in roles.
--
-- Adding the pages:* permission codes to meta.Permission (028_seed_page_permissions.sql)
-- does NOT retroactively grant them to any role — CreateAppCommandHandler only assigns
-- permissions to a role's built-in set at the moment that role/app is created. Every app
-- created before the Pages feature shipped therefore has an Administrator/Participant/
-- Viewer role with zero pages:* permissions, and would otherwise be permanently locked
-- out of Pages without manual per-role toggling. This migration is the one-time catch-up;
-- CreateAppCommandHandler has already been updated so all NEW apps get this by default.
--
-- Matches the same split CreateAppCommandHandler uses for new apps: Administrator gets
-- full control, Participant/Viewer get pages:read only.

INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT ar.Id, p.Id
FROM meta.AppRole ar
CROSS JOIN meta.Permission p
WHERE ar.Name = 'Administrator' AND ar.IsSystem = 1 AND ar.IsDeleted = 0
  AND p.Code IN ('pages:read', 'pages:create', 'pages:update', 'pages:delete', 'pages:publish', 'pages:code')
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp
      WHERE arp.AppRoleId = ar.Id AND arp.PermissionId = p.Id
  );
GO

INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT ar.Id, p.Id
FROM meta.AppRole ar
CROSS JOIN meta.Permission p
WHERE ar.Name IN ('Participant', 'Viewer') AND ar.IsSystem = 1 AND ar.IsDeleted = 0
  AND p.Code = 'pages:read'
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRolePermission arp
      WHERE arp.AppRoleId = ar.Id AND arp.PermissionId = p.Id
  );
GO
