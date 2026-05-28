IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.Form'))
BEGIN
    CREATE TABLE meta.Form (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId         BIGINT               NOT NULL,
        AppTableId       BIGINT               NOT NULL,
        Name             NVARCHAR(200)        NOT NULL,
        IsDefault        BIT                  NOT NULL DEFAULT 0,
        AutoAddNewFields BIT                  NOT NULL DEFAULT 1,
        ShowBuiltInFields BIT                 NOT NULL DEFAULT 0,
        SaveOptions      NVARCHAR(200)        NOT NULL DEFAULT 'SaveKeepWorking,SaveNew,SaveNext,SaveView',
        DisplayOrder     INT                  NOT NULL DEFAULT 0,
        IsDeleted        BIT                  NOT NULL DEFAULT 0,
        CreatedOn        DATETIME2(3)         NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy        BIGINT               NOT NULL DEFAULT 0,
        ModifiedOn       DATETIME2(3)         NULL,
        ModifiedBy       BIGINT               NULL,
        DeletedOn        DATETIME2(3)         NULL,
        DeletedBy        BIGINT               NULL,
        RowVersion       ROWVERSION           NOT NULL,
        CONSTRAINT PK_Form PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Form_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Form_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id)
    );

    CREATE NONCLUSTERED INDEX IX_Form_TenantId
        ON meta.Form (TenantId)
        WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IX_Form_AppTableId
        ON meta.Form (AppTableId)
        WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.FormSection'))
BEGIN
    CREATE TABLE meta.FormSection (
        Id           BIGINT           NOT NULL IDENTITY(1,1),
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId     BIGINT           NOT NULL,
        FormId       BIGINT           NOT NULL,
        Name         NVARCHAR(200)    NOT NULL DEFAULT 'Section heading',
        ColumnCount  TINYINT          NOT NULL DEFAULT 2,
        IsCollapsed  BIT              NOT NULL DEFAULT 0,
        DisplayOrder INT              NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormSection PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FormSection_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormSection_Form FOREIGN KEY (FormId) REFERENCES meta.Form(Id) ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX IX_FormSection_FormId ON meta.FormSection (FormId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.FormElement'))
BEGIN
    CREATE TABLE meta.FormElement (
        Id               BIGINT           NOT NULL IDENTITY(1,1),
        PublicId         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId         BIGINT           NOT NULL,
        FormSectionId    BIGINT           NOT NULL,
        AppFieldId       BIGINT           NOT NULL,
        LabelMode        VARCHAR(20)      NOT NULL DEFAULT 'Default',
        CustomLabel      NVARCHAR(200)    NULL,
        ShowOnAdd        BIT              NOT NULL DEFAULT 1,
        ShowOnEdit       BIT              NOT NULL DEFAULT 1,
        ShowOnView       BIT              NOT NULL DEFAULT 1,
        WidthMode        VARCHAR(20)      NOT NULL DEFAULT 'Auto',
        WidthValue       INT              NULL,
        HelpTextOverride NVARCHAR(500)    NULL,
        IsReadOnly       BIT              NOT NULL DEFAULT 0,
        IsRequired       BIT              NOT NULL DEFAULT 0,
        DisplayAs        VARCHAR(30)      NULL,
        DisplayOrder     INT              NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormElement PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FormElement_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormElement_FormSection FOREIGN KEY (FormSectionId) REFERENCES meta.FormSection(Id) ON DELETE CASCADE,
        CONSTRAINT FK_FormElement_AppField FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id)
    );

    CREATE NONCLUSTERED INDEX IX_FormElement_FormSectionId ON meta.FormElement (FormSectionId);
    CREATE NONCLUSTERED INDEX IX_FormElement_AppFieldId    ON meta.FormElement (AppFieldId);
END
GO
