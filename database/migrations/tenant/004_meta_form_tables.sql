-- Tenant DB baseline: Form, FormSection, FormSectionBlock, FormElement,
-- FormRule, AppRoleTableFormOverride (all without TenantId)

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Form')
BEGIN
    CREATE TABLE meta.Form (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWSEQUENTIALID(),
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
    CREATE NONCLUSTERED INDEX IX_Form_AppTableId ON meta.Form(AppTableId) WHERE IsDeleted = 0;
END
GO

-- Re-add FK from Report to Form now that Form exists
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Report_ViewEditForm')
BEGIN
    ALTER TABLE meta.Report ADD CONSTRAINT FK_Report_ViewEditForm FOREIGN KEY (ViewEditFormId) REFERENCES meta.Form(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FormSection')
BEGIN
    CREATE TABLE meta.FormSection (
        Id           BIGINT           NOT NULL IDENTITY(1,1),
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        FormId       BIGINT           NOT NULL,
        Name         NVARCHAR(200)    NOT NULL DEFAULT 'Section heading',
        ColumnCount  TINYINT          NOT NULL DEFAULT 2,
        ColumnWidths NVARCHAR(200)    NULL,
        IsCollapsed  BIT              NOT NULL DEFAULT 0,
        DisplayOrder INT              NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormSection PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FormSection_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormSection_Form FOREIGN KEY (FormId) REFERENCES meta.Form(Id) ON DELETE CASCADE
    );
    CREATE NONCLUSTERED INDEX IX_FormSection_FormId ON meta.FormSection(FormId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FormSectionBlock')
BEGIN
    CREATE TABLE meta.FormSectionBlock (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        PublicId        UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        FormSectionId   BIGINT           NOT NULL,
        Heading         NVARCHAR(200)    NULL,
        BackgroundColor NVARCHAR(9)      NULL,
        Width           INT              NULL,
        DisplayOrder    INT              NOT NULL,
        CONSTRAINT PK_FormSectionBlock PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_FormSectionBlock_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormSectionBlock_FormSection FOREIGN KEY (FormSectionId) REFERENCES meta.FormSection(Id) ON DELETE CASCADE
    );
    CREATE NONCLUSTERED INDEX IX_FormSectionBlock_FormSectionId ON meta.FormSectionBlock(FormSectionId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FormElement')
BEGIN
    CREATE TABLE meta.FormElement (
        Id               BIGINT           NOT NULL IDENTITY(1,1),
        PublicId         UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        FormSectionId    BIGINT           NOT NULL,
        FormSectionBlockId BIGINT         NULL,
        AppFieldId       BIGINT           NOT NULL,
        ElementType      VARCHAR(30)      NULL,
        ElementContent   NVARCHAR(MAX)    NULL,
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
        CONSTRAINT FK_FormElement_AppField   FOREIGN KEY (AppFieldId)    REFERENCES meta.AppField(Id),
        CONSTRAINT FK_FormElement_FormSectionBlock FOREIGN KEY (FormSectionBlockId) REFERENCES meta.FormSectionBlock(Id) ON DELETE NO ACTION
    );
    CREATE NONCLUSTERED INDEX IX_FormElement_FormSectionId ON meta.FormElement(FormSectionId);
    CREATE NONCLUSTERED INDEX IX_FormElement_AppFieldId    ON meta.FormElement(AppFieldId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'FormRule')
BEGIN
    CREATE TABLE meta.FormRule (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWSEQUENTIALID(),
        FormId           BIGINT               NOT NULL,
        Name             NVARCHAR(200)        NOT NULL,
        Description      NVARCHAR(500)        NULL,
        Tags             NVARCHAR(500)        NULL,
        IsActive         BIT                  NOT NULL DEFAULT 1,
        IsExpressionMode BIT                  NOT NULL DEFAULT 0,
        ExpressionText   NVARCHAR(MAX)        NULL,
        RunTrigger       VARCHAR(30)          NOT NULL DEFAULT 'AnyChange',
        ConditionLogic   VARCHAR(5)           NOT NULL DEFAULT 'all',
        DisplayOrder     INT                  NOT NULL DEFAULT 0,
        IsDeleted        BIT                  NOT NULL DEFAULT 0,
        CreatedOn        DATETIME2(3)         NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy        BIGINT               NOT NULL DEFAULT 0,
        ModifiedOn       DATETIME2(3)         NULL,
        ModifiedBy       BIGINT               NULL,
        DeletedOn        DATETIME2(3)         NULL,
        DeletedBy        BIGINT               NULL,
        RowVersion       ROWVERSION           NOT NULL,
        CONSTRAINT PK_FormRule PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FormRule_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_FormRule_Form FOREIGN KEY (FormId) REFERENCES meta.Form(Id) ON DELETE CASCADE
    );
    CREATE NONCLUSTERED INDEX IX_FormRule_FormId ON meta.FormRule(FormId) WHERE IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.FormRuleCondition'))
BEGIN
    CREATE TABLE meta.FormRuleCondition (
        Id           BIGINT        NOT NULL IDENTITY(1,1),
        FormRuleId   BIGINT        NOT NULL,
        AppFieldId   BIGINT        NOT NULL,
        Operator     VARCHAR(30)   NOT NULL,
        Value        NVARCHAR(500) NULL,
        ValueType    VARCHAR(30)   NULL,
        ValueFieldId BIGINT        NULL,
        DisplayOrder INT           NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormRuleCondition PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_FormRuleCondition_Rule  FOREIGN KEY (FormRuleId) REFERENCES meta.FormRule(Id) ON DELETE CASCADE,
        CONSTRAINT FK_FormRuleCondition_Field FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id)
    );
    CREATE NONCLUSTERED INDEX IX_FormRuleCondition_FormRuleId ON meta.FormRuleCondition(FormRuleId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.FormRuleAction'))
BEGIN
    CREATE TABLE meta.FormRuleAction (
        Id              BIGINT      NOT NULL IDENTITY(1,1),
        FormRuleId      BIGINT      NOT NULL,
        ActionType      VARCHAR(30) NOT NULL,
        TargetType      VARCHAR(20) NOT NULL,
        TargetElementId BIGINT      NULL,
        TargetSectionId BIGINT      NULL,
        TargetBlockId   BIGINT      NULL,
        ActionValue     NVARCHAR(500) NULL,
        DisplayOrder    INT         NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormRuleAction PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_FormRuleAction_Rule    FOREIGN KEY (FormRuleId)      REFERENCES meta.FormRule(Id)    ON DELETE CASCADE,
        CONSTRAINT FK_FormRuleAction_Element FOREIGN KEY (TargetElementId) REFERENCES meta.FormElement(Id) ON DELETE NO ACTION,
        CONSTRAINT FK_FormRuleAction_Section FOREIGN KEY (TargetSectionId) REFERENCES meta.FormSection(Id) ON DELETE NO ACTION
    );
    CREATE NONCLUSTERED INDEX IX_FormRuleAction_FormRuleId ON meta.FormRuleAction(FormRuleId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleTableFormOverride')
BEGIN
    CREATE TABLE meta.AppRoleTableFormOverride (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        AppTableId BIGINT NOT NULL,
        AppRoleId  BIGINT NULL,
        EditFormId BIGINT NULL,
        AddFormId  BIGINT NULL,
        CreatedOn  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy  BIGINT       NOT NULL,
        ModifiedOn DATETIME2(3) NULL,
        ModifiedBy BIGINT       NULL,
        CONSTRAINT PK_AppRoleTableFormOverride PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AppRole  FOREIGN KEY (AppRoleId)  REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_EditForm FOREIGN KEY (EditFormId) REFERENCES meta.Form(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AddForm  FOREIGN KEY (AddFormId)  REFERENCES meta.Form(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRoleTableFormOverride_TableRole ON meta.AppRoleTableFormOverride(AppTableId, AppRoleId);
END
GO
