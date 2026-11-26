-- Migration 103: Roles > Advanced Permissions.
--
-- 1. meta.AppRolePermissionTableExclusion — per-table narrowing of a role's builder permissions
--    (tables:read/update/delete, fields:*, reports:create/read/update/delete, forms:*, forms:rules:manage).
--    A role holds such a permission on EVERY table of the app unless a row here excludes that table. Storing the
--    exclusions (not the grants) means existing roles keep exactly the access they had (no rows), and a table created
--    later is covered by every role that holds the permission — no backfill on table creation.
-- 2. Branding permissions: which parts of App Settings > Branding & Layout a role may change.
--    Roles that could update the app before (apps:update) get all four, so nobody loses access.
-- 3. reports:run is no longer shown on the role page and is always granted: give it to every role.

IF OBJECT_ID('meta.AppRolePermissionTableExclusion', 'U') IS NULL
BEGIN
    CREATE TABLE meta.AppRolePermissionTableExclusion (
        AppRoleId    BIGINT NOT NULL,
        PermissionId BIGINT NOT NULL,
        AppTableId   BIGINT NOT NULL,
        CONSTRAINT PK_AppRolePermissionTableExclusion PRIMARY KEY CLUSTERED (AppRoleId, PermissionId, AppTableId),
        CONSTRAINT FK_AppRolePermissionTableExclusion_AppRole    FOREIGN KEY (AppRoleId)    REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRolePermissionTableExclusion_Permission FOREIGN KEY (PermissionId) REFERENCES meta.Permission(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'branding:color')
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES ('branding:color', 'Appearance', 'Change the app''s background mode and accent color');
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'branding:font')
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES ('branding:font', 'Fonts', 'Change the font family and size');
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'branding:grid')
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES ('branding:grid', 'Grid', 'Change grid density, borders and rows per page');
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'branding:layout')
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES ('branding:layout', 'Layout', 'Change the sidebar style, navigation position and layout');
GO

-- Backfill: roles holding apps:update get the four Branding permissions (only once, when none is present yet).
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT arp.AppRoleId, bp.Id
FROM meta.AppRolePermission arp
JOIN meta.Permission p  ON p.Id = arp.PermissionId AND p.Code = 'apps:update'
CROSS JOIN meta.Permission bp
WHERE bp.Code IN ('branding:color', 'branding:font', 'branding:grid', 'branding:layout')
  AND NOT EXISTS (SELECT 1 FROM meta.AppRolePermission x WHERE x.AppRoleId = arp.AppRoleId AND x.PermissionId = bp.Id);
GO

-- reports:run for every role that does not have it yet — but not roles with no permissions at all
-- (that is how "Disable access to app for users with this role" is stored).
INSERT INTO meta.AppRolePermission (AppRoleId, PermissionId)
SELECT r.Id, p.Id
FROM meta.AppRole r
CROSS JOIN meta.Permission p
WHERE p.Code = 'reports:run' AND r.IsDeleted = 0
  AND EXISTS (SELECT 1 FROM meta.AppRolePermission any_p WHERE any_p.AppRoleId = r.Id)
  AND NOT EXISTS (SELECT 1 FROM meta.AppRolePermission x WHERE x.AppRoleId = r.Id AND x.PermissionId = p.Id);
GO
