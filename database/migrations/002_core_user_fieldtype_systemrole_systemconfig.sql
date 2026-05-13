IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'SystemRole')
BEGIN
    CREATE TABLE core.SystemRole (
        Id      BIGINT IDENTITY(1,1) NOT NULL,
        Code    NVARCHAR(50)  NOT NULL,
        Name    NVARCHAR(100) NOT NULL,
        CONSTRAINT PK_SystemRole PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_SystemRole_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'FieldType')
BEGIN
    CREATE TABLE core.FieldType (
        Id       BIGINT IDENTITY(1,1) NOT NULL,
        Code     NVARCHAR(50)  NOT NULL,
        Name     NVARCHAR(100) NOT NULL,
        SqlType  NVARCHAR(100) NOT NULL,
        IsActive BIT           NOT NULL DEFAULT 1,
        CONSTRAINT PK_FieldType PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FieldType_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'User')
BEGIN
    CREATE TABLE core.[User] (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        Email        NVARCHAR(256) NOT NULL,
        PasswordHash NVARCHAR(256) NOT NULL,
        FirstName    NVARCHAR(100) NOT NULL,
        LastName     NVARCHAR(100) NOT NULL,
        SystemRoleId BIGINT        NOT NULL,
        IsActive     BIT           NOT NULL DEFAULT 1,
        IsDeleted    BIT           NOT NULL DEFAULT 0,
        CreatedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        RowVersion   ROWVERSION    NOT NULL,
        CONSTRAINT PK_User PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_User_PublicId UNIQUE (PublicId),
        CONSTRAINT UX_User_Email UNIQUE (Email),
        CONSTRAINT FK_User_SystemRole FOREIGN KEY (SystemRoleId) REFERENCES core.SystemRole(Id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'SystemConfig')
BEGIN
    CREATE TABLE core.SystemConfig (
        Id        BIGINT IDENTITY(1,1) NOT NULL,
        [Key]     NVARCHAR(100) NOT NULL,
        [Value]   NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SystemConfig PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_SystemConfig_Key UNIQUE ([Key])
    );
END
GO
