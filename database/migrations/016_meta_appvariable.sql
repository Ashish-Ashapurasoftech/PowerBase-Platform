IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppVariable')
BEGIN
    CREATE TABLE meta.AppVariable (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId       BIGINT        NOT NULL,
        TenantId    BIGINT        NOT NULL,
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
        CONSTRAINT FK_AppVariable_App    FOREIGN KEY (AppId)    REFERENCES meta.App(Id),
        CONSTRAINT FK_AppVariable_Tenant FOREIGN KEY (TenantId) REFERENCES meta.Tenant(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppVariable_AppId ON meta.AppVariable(AppId, TenantId) WHERE IsDeleted = 0;
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppVariable_AppName_Active ON meta.AppVariable(AppId, TenantId, Name) WHERE IsDeleted = 0;
END
GO
