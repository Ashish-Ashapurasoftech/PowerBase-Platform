IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRole')
BEGIN
    CREATE TABLE meta.AppRole (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId       BIGINT           NOT NULL,
        TenantId    BIGINT           NOT NULL,
        Name        NVARCHAR(100)    NOT NULL,
        IsDefault   BIT              NOT NULL DEFAULT 0,
        IsSystem    BIT              NOT NULL DEFAULT 0,
        CreatedOn   DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT           NOT NULL DEFAULT 0,
        IsDeleted   BIT              NOT NULL DEFAULT 0,
        RowVersion  ROWVERSION       NOT NULL,
        CONSTRAINT PK_AppRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRole_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_AppRole_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRole_AppId ON meta.AppRole(AppId) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_AppRole_TenantId ON meta.AppRole(TenantId) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppUser')
BEGIN
    CREATE TABLE meta.AppUser (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId       BIGINT           NOT NULL,
        TenantId    BIGINT           NOT NULL,
        UserId      BIGINT           NOT NULL,
        AppRoleId   BIGINT           NOT NULL,
        Status      NVARCHAR(20)     NOT NULL DEFAULT 'Active',
        AddedBy     BIGINT           NOT NULL,
        CreatedOn   DATETIME2(3)     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedOn   DATETIME2(3)     NULL,
        IsDeleted   BIT              NOT NULL DEFAULT 0,
        RowVersion  ROWVERSION       NOT NULL,
        CONSTRAINT PK_AppUser PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppUser_PublicId UNIQUE (PublicId),
        CONSTRAINT UX_AppUser_AppId_UserId UNIQUE (AppId, UserId),
        CONSTRAINT FK_AppUser_App     FOREIGN KEY (AppId)     REFERENCES meta.App(Id),
        CONSTRAINT FK_AppUser_User    FOREIGN KEY (UserId)    REFERENCES core.[User](Id),
        CONSTRAINT FK_AppUser_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppUser_AppId   ON meta.AppUser(AppId)   WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_AppUser_TenantId ON meta.AppUser(TenantId) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_AppUser_UserId   ON meta.AppUser(UserId)   WHERE IsDeleted = 0;
END
GO
