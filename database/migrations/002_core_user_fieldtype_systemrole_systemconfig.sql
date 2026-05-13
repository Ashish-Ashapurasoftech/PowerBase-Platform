IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'SystemRole')
BEGIN
    CREATE TABLE core.SystemRole (
        Id          INT IDENTITY(1,1) NOT NULL,
        Code        NVARCHAR(50)  NOT NULL,
        DisplayName NVARCHAR(100) NOT NULL,
        Description NVARCHAR(500) NULL,
        CONSTRAINT PK_SystemRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_SystemRole_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'FieldType')
BEGIN
    CREATE TABLE core.FieldType (
        Id               INT IDENTITY(1,1) NOT NULL,
        Code             NVARCHAR(50)  NOT NULL,
        DisplayName      NVARCHAR(100) NOT NULL,
        Category         NVARCHAR(50)  NOT NULL,
        SqlDataType      NVARCHAR(100) NOT NULL,
        SupportsDefault  BIT           NOT NULL DEFAULT 1,
        SupportsRequired BIT           NOT NULL DEFAULT 1,
        SupportsUnique   BIT           NOT NULL DEFAULT 1,
        DisplayOrder     INT           NOT NULL DEFAULT 0,
        IsActive         BIT           NOT NULL DEFAULT 1,
        CONSTRAINT PK_FieldType PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FieldType_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'User')
BEGIN
    CREATE TABLE core.[User] (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        Email            NVARCHAR(256) NOT NULL,
        EmailNormalized  NVARCHAR(256) NOT NULL,
        HashedPassword   NVARCHAR(256) NOT NULL,
        Name             NVARCHAR(200) NOT NULL,
        IsEmailVerified  BIT           NOT NULL DEFAULT 0,
        IsActive         BIT           NOT NULL DEFAULT 1,
        IsDeleted        BIT           NOT NULL DEFAULT 0,
        LastLoginOn      DATETIME2(3)  NULL,
        CreatedOn        DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy        BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn       DATETIME2(3)  NULL,
        ModifiedBy       BIGINT        NULL,
        DeletedOn        DATETIME2(3)  NULL,
        DeletedBy        BIGINT        NULL,
        RowVersion       ROWVERSION    NOT NULL,
        CONSTRAINT PK_User PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_User_PublicId UNIQUE (PublicId),
        CONSTRAINT UX_User_Email UNIQUE (Email)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'SystemConfig')
BEGIN
    CREATE TABLE core.SystemConfig (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        [Key]       NVARCHAR(100) NOT NULL,
        [Value]     NVARCHAR(MAX) NOT NULL,
        Description NVARCHAR(500) NULL,
        IsSecret    BIT           NOT NULL DEFAULT 0,
        ModifiedOn  DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedBy  BIGINT        NULL,
        CONSTRAINT PK_SystemConfig PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_SystemConfig_Key UNIQUE ([Key])
    );
END
GO
