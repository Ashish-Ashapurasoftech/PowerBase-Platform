IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'App')
BEGIN
    CREATE TABLE meta.App (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId    BIGINT        NOT NULL,
        OwnerId     BIGINT        NOT NULL,
        Name        NVARCHAR(200) NOT NULL,
        Description NVARCHAR(500) NULL,
        Icon        NVARCHAR(100) NULL,
        Color       NVARCHAR(20)  NULL,
        Status      VARCHAR(20)   NOT NULL DEFAULT 'Active',
        IsDeleted   BIT           NOT NULL DEFAULT 0,
        CreatedOn   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn  DATETIME2(3)  NULL,
        ModifiedBy  BIGINT        NULL,
        DeletedOn   DATETIME2(3)  NULL,
        DeletedBy   BIGINT        NULL,
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
        Id                BIGINT IDENTITY(1,1) NOT NULL,
        PublicId          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId          BIGINT        NOT NULL,
        AppId             BIGINT        NOT NULL,
        Name              NVARCHAR(200) NOT NULL,
        SingularLabel     NVARCHAR(200) NULL,
        PluralLabel       NVARCHAR(200) NULL,
        Description       NVARCHAR(500) NULL,
        PhysicalTableName NVARCHAR(100) NULL,
        DisplayFieldId    BIGINT        NULL,
        RecordCount       INT           NOT NULL DEFAULT 0,
        IsSystem          BIT           NOT NULL DEFAULT 0,
        DisplayOrder      INT           NOT NULL DEFAULT 0,
        IsDeleted         BIT           NOT NULL DEFAULT 0,
        CreatedOn         DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy         BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn        DATETIME2(3)  NULL,
        ModifiedBy        BIGINT        NULL,
        DeletedOn         DATETIME2(3)  NULL,
        DeletedBy         BIGINT        NULL,
        RowVersion        ROWVERSION    NOT NULL,
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
        Id                 BIGINT IDENTITY(1,1) NOT NULL,
        PublicId           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId           BIGINT        NOT NULL,
        AppTableId         BIGINT        NOT NULL,
        FieldTypeId        INT           NOT NULL,
        Name               NVARCHAR(200) NOT NULL,
        Label              NVARCHAR(200) NULL,
        HelpText           NVARCHAR(500) NULL,
        Placeholder        NVARCHAR(200) NULL,
        Description        NVARCHAR(500) NULL,
        PhysicalColumnName NVARCHAR(100) NULL,
        DefaultValue       NVARCHAR(500) NULL,
        MaxLength          INT           NULL,
        Precision          INT           NULL,
        Scale              INT           NULL,
        DisplayOrder       INT           NOT NULL DEFAULT 0,
        IsRequired         BIT           NOT NULL DEFAULT 0,
        --IsRequiredInForm   BIT           NOT NULL DEFAULT 0,
        IsUnique           BIT           NOT NULL DEFAULT 0,
        IsPrimary          BIT           NOT NULL DEFAULT 0,
        IsSystem           BIT           NOT NULL DEFAULT 0,
        IsSearchable       BIT           NOT NULL DEFAULT 0,
        IsSortable         BIT           NOT NULL DEFAULT 1,
        IsFilterable       BIT           NOT NULL DEFAULT 1,
        IsReportable       BIT           NOT NULL DEFAULT 1,
        Settings           NVARCHAR(MAX) NULL,
        IsDeleted          BIT           NOT NULL DEFAULT 0,
        CreatedOn          DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy          BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn         DATETIME2(3)  NULL,
        ModifiedBy         BIGINT        NULL,
        DeletedOn          DATETIME2(3)  NULL,
        DeletedBy          BIGINT        NULL,
        CONSTRAINT PK_AppField PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppField_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_AppField_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_AppField_FieldType FOREIGN KEY (FieldTypeId) REFERENCES core.FieldType(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppField_AppTableId ON meta.AppField(AppTableId) WHERE IsDeleted = 0;
END
GO
