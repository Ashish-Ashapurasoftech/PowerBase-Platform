IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Tenant')
BEGIN
    CREATE TABLE meta.Tenant (
        Id        BIGINT IDENTITY(1,1) NOT NULL,
        PublicId  UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        Name      NVARCHAR(200) NOT NULL,
        Slug      NVARCHAR(100) NOT NULL,
        Status    TINYINT       NOT NULL DEFAULT 0,
        IsDeleted BIT           NOT NULL DEFAULT 0,
        CreatedAt DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowVersion ROWVERSION   NOT NULL,
        CONSTRAINT PK_Tenant PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Tenant_PublicId UNIQUE (PublicId),
        CONSTRAINT UX_Tenant_Slug UNIQUE (Slug)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'TenantRole')
BEGIN
    CREATE TABLE meta.TenantRole (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        TenantId    BIGINT        NOT NULL,
        Name        NVARCHAR(100) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsDefault   BIT           NOT NULL DEFAULT 0,
        CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TenantRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_TenantRole_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'TenantUser')
BEGIN
    CREATE TABLE meta.TenantUser (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT    NOT NULL,
        UserId       BIGINT    NOT NULL,
        TenantRoleId BIGINT    NOT NULL,
        IsActive     BIT       NOT NULL DEFAULT 1,
        CreatedAt    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_TenantUser PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_TenantUser_TenantUser UNIQUE (TenantId, UserId),
        CONSTRAINT FK_TenantUser_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id),
        CONSTRAINT FK_TenantUser_User FOREIGN KEY (UserId) REFERENCES core.[User](Id),
        CONSTRAINT FK_TenantUser_TenantRole FOREIGN KEY (TenantRoleId) REFERENCES meta.TenantRole(Id)
    );
END
GO
