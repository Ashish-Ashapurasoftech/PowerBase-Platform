-- Seed: pages:* permission codes for the Pages feature.
-- pages:read is a default-user permission (see PermissionCodes.DefaultUserPermissions);
-- the rest are builder-only. pages:code is the stricter Code Page Builder capability —
-- deliberately separate from pages:create/update so Code pages can be gated independently.
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'pages:read')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES
        ('pages:read',    'View Pages',          'View the Pages list and open published pages'),
        ('pages:create',  'Create Pages',        'Create and duplicate pages'),
        ('pages:update',  'Update Pages',        'Edit pages, restore versions, view drafts'),
        ('pages:delete',  'Delete Pages',        'Soft-delete pages'),
        ('pages:publish', 'Publish Pages',       'Publish and unpublish pages'),
        ('pages:code',    'Code Page Builder',   'Create and edit Code-type pages (custom HTML/CSS/JS)');
END
GO
