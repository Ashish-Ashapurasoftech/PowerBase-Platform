-- Tenant DB baseline: seed Permission catalogue (app-level permission codes).
-- TenantRole and RolePermission are control-plane — they are seeded by CreateTenantCommandHandler.

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'apps:create')
BEGIN
    SET IDENTITY_INSERT meta.Permission ON;
    INSERT INTO meta.Permission (Id, Code, DisplayName, Description) VALUES
        (1,  'apps:create',        'Create Apps',           'Create new applications'),
        (2,  'apps:read',          'View Apps',             'View application list and details'),
        (3,  'apps:update',        'Update Apps',           'Modify application settings'),
        (4,  'apps:delete',        'Delete Apps',           'Soft-delete applications'),
        (5,  'tables:create',      'Create Tables',         'Create tables inside an application'),
        (6,  'tables:read',        'View Tables',           'View table list and structure'),
        (7,  'tables:update',      'Update Tables',         'Modify table settings'),
        (8,  'tables:delete',      'Delete Tables',         'Soft-delete tables'),
        (9,  'fields:create',      'Create Fields',         'Add fields to a table'),
        (10, 'fields:read',        'View Fields',           'View field definitions'),
        (11, 'fields:update',      'Update Fields',         'Modify field settings'),
        (12, 'fields:delete',      'Delete Fields',         'Soft-delete fields'),
        (13, 'records:create',     'Create Records',        'Insert new records'),
        (14, 'records:read',       'View Records',          'Read records from a table'),
        (15, 'records:update',     'Update Records',        'Edit existing records'),
        (16, 'records:delete',     'Delete Records',        'Soft-delete records'),
        (17, 'reports:create',     'Create Reports',        'Save report definitions'),
        (18, 'reports:read',       'View Reports',          'View report definitions'),
        (19, 'reports:update',     'Update Reports',        'Edit report definitions'),
        (20, 'reports:delete',     'Delete Reports',        'Remove report definitions'),
        (21, 'reports:run',        'Run Reports',           'Execute reports and view results'),
        (22, 'users:invite',       'Invite Users',          'Add users to the tenant'),
        (23, 'users:manage',       'Manage Users',          'Change user roles and remove users'),
        (24, 'roles:manage',       'Manage Roles',          'Create roles and assign permissions'),
        (25, 'audit:read',         'View Audit Logs',       'Read activity logs'),
        (26, 'forms:create',       'Create Forms',          'Create new forms'),
        (27, 'forms:read',         'View Forms',            'View form definitions'),
        (28, 'forms:update',       'Update Forms',          'Modify form settings and layout'),
        (29, 'forms:delete',       'Delete Forms',          'Remove forms'),
        (30, 'forms:rules:manage', 'Manage Form Rules',     'Create and edit form automation rules');
    SET IDENTITY_INSERT meta.Permission OFF;
END

GO
