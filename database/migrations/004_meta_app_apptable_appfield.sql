IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'App')
BEGIN
    CREATE TABLE meta.App (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId    BIGINT        NOT NULL,
        OwnerId     BIGINT        NOT NULL,
        Name        NVARCHAR(200) NOT NULL,
        Description NVARCHAR(500) NULL,
        Status      TINYINT       NOT NULL DEFAULT 0,
        IsDeleted   BIT           NOT NULL DEFAULT 0,
        CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowVersion  ROWVERSION    NOT NULL,
        CONSTRAINT PK_App PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_App_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_App_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id),
        CONSTRAINT FK_App_Owner FOREIGN KEY (OwnerId) REFERENCES core.[User](Id)
    );
    CREATE NONCLUSTERED INDEX IX_App_TenantId ON meta.App(TenantId) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppTable')
BEGIN
    CREATE TABLE meta.AppTable (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        TenantId    BIGINT        NOT NULL,
        AppId       BIGINT        NOT NULL,
        Name        NVARCHAR(200) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsDeleted   BIT           NOT NULL DEFAULT 0,
        CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowVersion  ROWVERSION    NOT NULL,
        CONSTRAINT PK_AppTable PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppTable_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_AppTable_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppTable_AppId ON meta.AppTable(AppId) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppField')
BEGIN
    CREATE TABLE meta.AppField (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT        NOT NULL,
        AppTableId   BIGINT        NOT NULL,
        FieldTypeId  BIGINT        NOT NULL,
        Name         NVARCHAR(200) NOT NULL,
        Description  NVARCHAR(500) NULL,
        IsRequired   BIT           NOT NULL DEFAULT 0,
        DisplayOrder INT           NOT NULL DEFAULT 0,
        IsDeleted    BIT           NOT NULL DEFAULT 0,
        CreatedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_AppField PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_AppField_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_AppField_FieldType FOREIGN KEY (FieldTypeId) REFERENCES core.FieldType(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppField_AppTableId ON meta.AppField(AppTableId) WHERE IsDeleted = 0;
END
GO
