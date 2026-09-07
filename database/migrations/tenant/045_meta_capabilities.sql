-- Create meta.Capability table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Capability')
BEGIN
    CREATE TABLE meta.Capability (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        Code         VARCHAR(50)          NOT NULL,
        Name         VARCHAR(100)         NOT NULL,
        Icon         VARCHAR(50)          NOT NULL,
        Description  NVARCHAR(500)        NULL,
        DisplayOrder INT                  NOT NULL DEFAULT 1,
        IsActive     BIT                  NOT NULL DEFAULT 1,
        CONSTRAINT PK_Capability PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Capability_Code UNIQUE (Code)
    );
END
GO

-- Create meta.CapabilityPermission table
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'CapabilityPermission')
BEGIN
    CREATE TABLE meta.CapabilityPermission (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        CapabilityId BIGINT               NOT NULL,
        PermissionId BIGINT               NOT NULL,
        CONSTRAINT PK_CapabilityPermission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_CapabilityPermission UNIQUE (CapabilityId, PermissionId),
        CONSTRAINT FK_CapabilityPermission_Capability FOREIGN KEY (CapabilityId) REFERENCES meta.Capability(Id) ON DELETE CASCADE,
        CONSTRAINT FK_CapabilityPermission_Permission FOREIGN KEY (PermissionId) REFERENCES meta.Permission(Id) ON DELETE CASCADE
    );
    CREATE NONCLUSTERED INDEX IX_CapabilityPermission_CapabilityId ON meta.CapabilityPermission(CapabilityId);
END
GO

-- Seed default capabilities
IF NOT EXISTS (SELECT 1 FROM meta.Capability WHERE Code = 'schema')
BEGIN
    INSERT INTO meta.Capability (Code, Name, Icon, Description, DisplayOrder, IsActive) VALUES
    ('schema', 'Schema Builder', 'pi pi-database', 'Controls database structure only (Tables & Fields). Does not grant access to view or edit table records.', 1, 1),
    ('form', 'Form Builder', 'pi pi-th-large', 'Controls user interface layouts (Form Designer & Dynamic Form Rules).', 2, 1),
    ('report', 'Report & Chart Builder', 'pi pi-chart-bar', 'Controls analytics, reports & charts. Safely displays summaries/aggregations without exposing raw record data.', 3, 1),
    ('automation', 'Automation Builder (PowerFlows)', 'pi pi-bolt', 'Controls workflow automation pipelines and dynamic form rules.', 4, 1),
    ('security', 'Security & Role Manager', 'pi pi-shield', 'Controls permissions, member invitations, and role management within configured hierarchy.', 5, 1);
END
GO

-- Seed CapabilityPermission mappings
-- Schema Builder
INSERT INTO meta.CapabilityPermission (CapabilityId, PermissionId)
SELECT c.Id, p.Id
FROM meta.Capability c
JOIN meta.Permission p ON p.Code IN (
    'apps:read',
    'tables:create', 'tables:read', 'tables:update', 'tables:delete',
    'fields:create', 'fields:read', 'fields:update', 'fields:delete'
)
WHERE c.Code = 'schema'
  AND NOT EXISTS (
      SELECT 1 FROM meta.CapabilityPermission cp
      WHERE cp.CapabilityId = c.Id AND cp.PermissionId = p.Id
  );
GO

-- Form Builder
INSERT INTO meta.CapabilityPermission (CapabilityId, PermissionId)
SELECT c.Id, p.Id
FROM meta.Capability c
JOIN meta.Permission p ON p.Code IN (
    'apps:read',
    'tables:read',
    'fields:read',
    'forms:create', 'forms:read', 'forms:update', 'forms:delete', 'forms:rules:manage'
)
WHERE c.Code = 'form'
  AND NOT EXISTS (
      SELECT 1 FROM meta.CapabilityPermission cp
      WHERE cp.CapabilityId = c.Id AND cp.PermissionId = p.Id
  );
GO

-- Report Builder
INSERT INTO meta.CapabilityPermission (CapabilityId, PermissionId)
SELECT c.Id, p.Id
FROM meta.Capability c
JOIN meta.Permission p ON p.Code IN (
    'apps:read',
    'tables:read',
    'fields:read',
    'reports:create', 'reports:read', 'reports:update', 'reports:delete', 'reports:run'
)
WHERE c.Code = 'report'
  AND NOT EXISTS (
      SELECT 1 FROM meta.CapabilityPermission cp
      WHERE cp.CapabilityId = c.Id AND cp.PermissionId = p.Id
  );
GO

-- Automation Builder
INSERT INTO meta.CapabilityPermission (CapabilityId, PermissionId)
SELECT c.Id, p.Id
FROM meta.Capability c
JOIN meta.Permission p ON p.Code IN (
    'apps:read',
    'tables:read',
    'fields:read',
    'PowerFlows:create', 'PowerFlows:read', 'PowerFlows:update', 'PowerFlows:delete', 'PowerFlows:copy',
    'forms:rules:manage'
)
WHERE c.Code = 'automation'
  AND NOT EXISTS (
      SELECT 1 FROM meta.CapabilityPermission cp
      WHERE cp.CapabilityId = c.Id AND cp.PermissionId = p.Id
  );
GO

-- Security & Role Manager
INSERT INTO meta.CapabilityPermission (CapabilityId, PermissionId)
SELECT c.Id, p.Id
FROM meta.Capability c
JOIN meta.Permission p ON p.Code IN (
    'apps:read',
    'roles:manage', 'users:manage', 'users:invite'
)
WHERE c.Code = 'security'
  AND NOT EXISTS (
      SELECT 1 FROM meta.CapabilityPermission cp
      WHERE cp.CapabilityId = c.Id AND cp.PermissionId = p.Id
  );
GO
