-- Tenant DB baseline: clean meta tables (no TenantId columns — one tenant per DB)
-- Note: meta.TenantRole and meta.RolePermission are control-plane tables (stay in shared DB).
-- meta.Permission is replicated here as a catalogue for meta.AppRolePermission FK.

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

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'App')
BEGIN
    CREATE TABLE meta.App (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        PublicId       UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        OwnerId        BIGINT        NOT NULL,
        OwnerName      NVARCHAR(256) NULL,
        Name           NVARCHAR(200) NOT NULL,
        Description    NVARCHAR(500) NULL,
        Icon           NVARCHAR(100) NULL,
        Color          NVARCHAR(20)  NULL,
        Status         VARCHAR(20)   NOT NULL DEFAULT 'Active',
        Formatting     NVARCHAR(MAX) NULL,
        SecurityOptions NVARCHAR(MAX) NULL,
        DefaultAppRoleId BIGINT      NULL,
        IsDeleted      BIT           NOT NULL DEFAULT 0,
        CreatedOn      DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy      BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn     DATETIME2(3)  NULL,
        ModifiedBy     BIGINT        NULL,
        DeletedOn      DATETIME2(3)  NULL,
        DeletedBy      BIGINT        NULL,
        RowVersion     ROWVERSION    NOT NULL,
        CONSTRAINT PK_App PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_App_PublicId UNIQUE (PublicId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppTable')
BEGIN
    CREATE TABLE meta.AppTable (
        Id                   BIGINT IDENTITY(1,1) NOT NULL,
        PublicId             UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId                BIGINT        NOT NULL,
        Name                 NVARCHAR(200) NOT NULL,
        SingularLabel        NVARCHAR(200) NULL,
        PluralLabel          NVARCHAR(200) NULL,
        Description          NVARCHAR(500) NULL,
        PhysicalTableName    NVARCHAR(100) NULL,
        DefaultReportSettings NVARCHAR(MAX) NULL,
        DisplayFieldId       BIGINT        NULL,
        RecordCount          INT           NOT NULL DEFAULT 0,
        IsSystem             BIT           NOT NULL DEFAULT 0,
        DisplayOrder         INT           NOT NULL DEFAULT 0,
        Icon                 NVARCHAR(100) NULL,
        IsDeleted            BIT           NOT NULL DEFAULT 0,
        CreatedOn            DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy            BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn           DATETIME2(3)  NULL,
        ModifiedBy           BIGINT        NULL,
        DeletedOn            DATETIME2(3)  NULL,
        DeletedBy            BIGINT        NULL,
        RowVersion           ROWVERSION    NOT NULL,
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
        IsRequiredInForm   BIT           NOT NULL DEFAULT 0,
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

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRole')
BEGIN
    CREATE TABLE meta.AppRole (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        PublicId   UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId      BIGINT        NOT NULL,
        Name       NVARCHAR(100) NOT NULL,
        IsDefault  BIT           NOT NULL DEFAULT 0,
        IsSystem   BIT           NOT NULL DEFAULT 0,
        CreatedOn  DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy  BIGINT        NOT NULL DEFAULT 0,
        IsDeleted  BIT           NOT NULL DEFAULT 0,
        RowVersion ROWVERSION    NOT NULL,
        CONSTRAINT PK_AppRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRole_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_AppRole_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRole_AppId ON meta.AppRole(AppId) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRolePermission')
BEGIN
    CREATE TABLE meta.AppRolePermission (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        AppRoleId   BIGINT NOT NULL,
        PermissionId BIGINT NOT NULL,
        CONSTRAINT PK_AppRolePermission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRolePermission UNIQUE (AppRoleId, PermissionId),
        CONSTRAINT FK_AppRolePermission_AppRole    FOREIGN KEY (AppRoleId)    REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRolePermission_Permission FOREIGN KEY (PermissionId) REFERENCES meta.Permission(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppUser')
BEGIN
    CREATE TABLE meta.AppUser (
        Id        BIGINT IDENTITY(1,1) NOT NULL,
        PublicId  UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId       BIGINT           NOT NULL,
        UserId      BIGINT           NOT NULL,
        UserPublicId UNIQUEIDENTIFIER NULL,
        UserName    NVARCHAR(256)    NULL,
        UserEmail   NVARCHAR(256)    NULL,
        AppRoleId   BIGINT           NOT NULL,
        Status    NVARCHAR(20)  NOT NULL DEFAULT 'Active',
        AddedBy   BIGINT        NOT NULL,
        CreatedOn DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedOn DATETIME2(3)  NULL,
        IsDeleted BIT           NOT NULL DEFAULT 0,
        RowVersion ROWVERSION   NOT NULL,
        CONSTRAINT PK_AppUser PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppUser_PublicId   UNIQUE (PublicId),
        CONSTRAINT UX_AppUser_AppId_UserId UNIQUE (AppId, UserId),
        CONSTRAINT FK_AppUser_App     FOREIGN KEY (AppId)     REFERENCES meta.App(Id),
        CONSTRAINT FK_AppUser_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppUser_AppId   ON meta.AppUser(AppId)   WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_AppUser_UserId  ON meta.AppUser(UserId)  WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppVariable')
BEGIN
    CREATE TABLE meta.AppVariable (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId       BIGINT        NOT NULL,
        Name        NVARCHAR(100) NOT NULL,
        Value       NVARCHAR(500) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsDeleted   BIT           NOT NULL DEFAULT 0,
        CreatedOn   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn  DATETIME2(3)  NULL,
        ModifiedBy  BIGINT        NULL,
        DeletedOn   DATETIME2(3)  NULL,
        DeletedBy   BIGINT        NULL,
        RowVersion  ROWVERSION    NOT NULL,
        CONSTRAINT PK_AppVariable PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppVariable_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_AppVariable_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppVariable_AppName_Active ON meta.AppVariable(AppId, Name) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Report')
BEGIN
    CREATE TABLE meta.Report (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppTableId   BIGINT        NOT NULL,
        OwnerId      BIGINT        NOT NULL,
        Name         NVARCHAR(200) NOT NULL,
        Description  NVARCHAR(500) NULL,
        ReportType   VARCHAR(20)   NOT NULL DEFAULT 'Table',
        Visibility   VARCHAR(20)   NOT NULL DEFAULT 'Personal',
        Definition   NVARCHAR(MAX) NOT NULL DEFAULT '{}',
        IsDefault    BIT           NOT NULL DEFAULT 0,
        DisplayOrder INT           NOT NULL DEFAULT 0,
        ViewEditFormId BIGINT      NULL,
        IsDeleted    BIT           NOT NULL DEFAULT 0,
        CreatedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy    BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn   DATETIME2(3)  NULL,
        ModifiedBy   BIGINT        NULL,
        DeletedOn    DATETIME2(3)  NULL,
        DeletedBy    BIGINT        NULL,
        RowVersion   ROWVERSION    NOT NULL,
        CONSTRAINT PK_Report PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Report_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Report_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleReport')
BEGIN
    CREATE TABLE meta.AppRoleReport (
        Id        BIGINT IDENTITY(1,1) NOT NULL,
        ReportId  BIGINT NOT NULL,
        AppRoleId BIGINT NOT NULL,
        CONSTRAINT PK_AppRoleReport PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRoleReport UNIQUE (ReportId, AppRoleId),
        CONSTRAINT FK_AppRoleReport_Report  FOREIGN KEY (ReportId)  REFERENCES meta.Report(Id),
        CONSTRAINT FK_AppRoleReport_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );
END
GO
