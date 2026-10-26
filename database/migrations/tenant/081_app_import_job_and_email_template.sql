-- Large app imports (QBL/PBL files over 3 MB) run in the background. One row per upload: who started it, how far it
-- got, and (when finished) which app it produced and the full report. Content holds the uploaded file until the job
-- finishes and is cleared afterwards.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppImportJob')
BEGIN
    CREATE TABLE meta.AppImportJob (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        PublicId        UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppImportJob_PublicId DEFAULT NEWID(),
        UserId          BIGINT        NOT NULL,
        FileName        NVARCHAR(260) NOT NULL,
        FileSizeBytes   BIGINT        NOT NULL,
        Status          VARCHAR(20)   NOT NULL,            -- Queued | Running | Completed | Failed
        Stage           NVARCHAR(200) NULL,
        PercentComplete INT           NOT NULL CONSTRAINT DF_AppImportJob_Pct DEFAULT 0,
        AppPublicId     UNIQUEIDENTIFIER NULL,
        AppName         NVARCHAR(200) NULL,
        ResultJson      NVARCHAR(MAX) NULL,
        ErrorMessage    NVARCHAR(2000) NULL,
        Content         VARBINARY(MAX) NULL,
        MappingJson     NVARCHAR(MAX) NULL,
        FrontendBaseUrl NVARCHAR(500) NULL,
        EmailSentOn     DATETIME2(3)  NULL,
        EmailError      NVARCHAR(500) NULL,
        IsDismissed     BIT           NOT NULL CONSTRAINT DF_AppImportJob_Dismissed DEFAULT 0,
        CreatedOn       DATETIME2(3)  NOT NULL CONSTRAINT DF_AppImportJob_CreatedOn DEFAULT SYSUTCDATETIME(),
        StartedOn       DATETIME2(3)  NULL,
        CompletedOn     DATETIME2(3)  NULL,
        CONSTRAINT PK_AppImportJob PRIMARY KEY CLUSTERED (Id)
    );
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppImportJob_PublicId ON meta.AppImportJob (PublicId);
    CREATE NONCLUSTERED INDEX IX_AppImportJob_User_Status ON meta.AppImportJob (UserId, IsDismissed, Status);
END
GO

-- Tenant-editable email templates (subject + HTML body with {{Placeholders}}). A missing row means "use the built-in default".
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'EmailTemplate')
BEGIN
    CREATE TABLE meta.EmailTemplate (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TemplateKey  VARCHAR(100)   NOT NULL,
        Subject      NVARCHAR(300)  NOT NULL,
        Body         NVARCHAR(MAX)  NOT NULL,
        IsEnabled    BIT            NOT NULL CONSTRAINT DF_EmailTemplate_Enabled DEFAULT 1,
        ModifiedOn   DATETIME2(3)   NOT NULL CONSTRAINT DF_EmailTemplate_ModifiedOn DEFAULT SYSUTCDATETIME(),
        ModifiedBy   BIGINT         NULL,
        CONSTRAINT PK_EmailTemplate PRIMARY KEY CLUSTERED (Id)
    );
    CREATE UNIQUE NONCLUSTERED INDEX UX_EmailTemplate_Key ON meta.EmailTemplate (TemplateKey);
END
GO
