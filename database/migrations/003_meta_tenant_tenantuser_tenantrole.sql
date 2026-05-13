IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Tenant')
BEGIN
    CREATE TABLE meta.Tenant (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        PublicId   UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        Name       NVARCHAR(200) NOT NULL,
        Slug       NVARCHAR(100) NOT NULL,
        PlanCode   NVARCHAR(50)  NOT NULL DEFAULT 'Free',
        Status     VARCHAR(20)   NOT NULL DEFAULT 'Active',
        IsDeleted  BIT           NOT NULL DEFAULT 0,
        CreatedOn  DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy  BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn DATETIME2(3)  NULL,
        ModifiedBy BIGINT        NULL,
        DeletedOn  DATETIME2(3)  NULL,
        DeletedBy  BIGINT        NULL,
        RowVersion ROWVERSION    NOT NULL,
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
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId    BIGINT        NOT NULL,
        Name        NVARCHAR(100) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsDefault   BIT           NOT NULL DEFAULT 0,
        IsSystem    BIT           NOT NULL DEFAULT 0,
        IsDeleted   BIT           NOT NULL DEFAULT 0,
        CreatedOn   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn  DATETIME2(3)  NULL,
        ModifiedBy  BIGINT        NULL,
        DeletedOn   DATETIME2(3)  NULL,
        DeletedBy   BIGINT        NULL,
        CONSTRAINT PK_TenantRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_TenantRole_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_TenantRole_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'TenantUser')
BEGIN
    CREATE TABLE meta.TenantUser (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT        NOT NULL,
        UserId       BIGINT        NOT NULL,
        TenantRoleId BIGINT        NULL,
        IsOwner      BIT           NOT NULL DEFAULT 0,
        IsActive     BIT           NOT NULL DEFAULT 1,
        IsDeleted    BIT           NOT NULL DEFAULT 0,
        JoinedOn     DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        InvitedBy    BIGINT        NULL,
        CreatedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy    BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn   DATETIME2(3)  NULL,
        ModifiedBy   BIGINT        NULL,
        DeletedOn    DATETIME2(3)  NULL,
        DeletedBy    BIGINT        NULL,
        RowVersion   ROWVERSION    NOT NULL,
        CONSTRAINT PK_TenantUser PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_TenantUser_TenantUser UNIQUE (TenantId, UserId),
        CONSTRAINT FK_TenantUser_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id),
        CONSTRAINT FK_TenantUser_User FOREIGN KEY (UserId) REFERENCES core.[User](Id),
        CONSTRAINT FK_TenantUser_TenantRole FOREIGN KEY (TenantRoleId) REFERENCES meta.TenantRole(Id)
    );
END
GO
