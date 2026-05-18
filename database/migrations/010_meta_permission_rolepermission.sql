IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Permission')
BEGIN
    CREATE TABLE meta.Permission (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        Code        NVARCHAR(100) NOT NULL,
        DisplayName NVARCHAR(200) NOT NULL,
        Description NVARCHAR(500) NULL,
        CONSTRAINT PK_Permission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Permission_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'RolePermission')
BEGIN
    CREATE TABLE meta.RolePermission (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantRoleId BIGINT NOT NULL,
        PermissionId BIGINT NOT NULL,
        CONSTRAINT PK_RolePermission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_RolePermission UNIQUE (TenantRoleId, PermissionId),
        CONSTRAINT FK_RolePermission_TenantRole FOREIGN KEY (TenantRoleId) REFERENCES meta.TenantRole(Id),
        CONSTRAINT FK_RolePermission_Permission  FOREIGN KEY (PermissionId)  REFERENCES meta.Permission(Id)
    );
END
GO

-- Seed permission catalogue
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'apps:create')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('apps:create',    'Create Apps',           'Create new applications in the tenant'),
        ('apps:read',      'View Apps',             'View application list and details'),
        ('apps:update',    'Update Apps',           'Modify application settings'),
        ('apps:delete',    'Delete Apps',           'Soft-delete applications'),
        ('tables:create',  'Create Tables',         'Create tables inside an application'),
        ('tables:read',    'View Tables',           'View table list and structure'),
        ('tables:delete',  'Delete Tables',         'Soft-delete tables'),
        ('fields:create',  'Create Fields',         'Add fields to a table'),
        ('fields:read',    'View Fields',           'View field definitions'),
        ('records:create', 'Create Records',        'Insert new records into a table'),
        ('records:read',   'View Records',          'Read records from a table'),
        ('records:update', 'Update Records',        'Edit existing records'),
        ('records:delete', 'Delete Records',        'Soft-delete records'),
        ('reports:create', 'Create Reports',        'Save report definitions'),
        ('reports:read',   'View Reports',          'View report definitions'),
        ('reports:run',    'Run Reports',           'Execute reports and view results'),
        ('users:invite',   'Invite Users',          'Add existing users to the tenant'),
        ('users:manage',   'Manage Users',          'Change user roles and remove users'),
        ('roles:manage',   'Manage Roles',          'Create roles and assign permissions');
END
GO
