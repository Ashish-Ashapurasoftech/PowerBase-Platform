IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID('meta.FormRule'))
BEGIN
    CREATE TABLE meta.FormRule (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        PublicId         UNIQUEIDENTIFIER     NOT NULL DEFAULT NEWSEQUENTIALID(),
        TenantId         BIGINT               NOT NULL,
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

    CREATE NONCLUSTERED INDEX IX_FormRule_TenantId
        ON meta.FormRule (TenantId)
        WHERE IsDeleted = 0;

    CREATE NONCLUSTERED INDEX IX_FormRule_FormId
        ON meta.FormRule (FormId)
        WHERE IsDeleted = 0;
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
        DisplayOrder INT           NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormRuleCondition PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_FormRuleCondition_Rule  FOREIGN KEY (FormRuleId) REFERENCES meta.FormRule(Id) ON DELETE CASCADE,
        CONSTRAINT FK_FormRuleCondition_Field FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id)
    );

    CREATE NONCLUSTERED INDEX IX_FormRuleCondition_FormRuleId ON meta.FormRuleCondition (FormRuleId);
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
        DisplayOrder    INT         NOT NULL DEFAULT 0,
        CONSTRAINT PK_FormRuleAction PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_FormRuleAction_Rule    FOREIGN KEY (FormRuleId)      REFERENCES meta.FormRule(Id)    ON DELETE CASCADE,
        CONSTRAINT FK_FormRuleAction_Element FOREIGN KEY (TargetElementId) REFERENCES meta.FormElement(Id) ON DELETE NO ACTION,
        CONSTRAINT FK_FormRuleAction_Section FOREIGN KEY (TargetSectionId) REFERENCES meta.FormSection(Id) ON DELETE NO ACTION
    );

    CREATE NONCLUSTERED INDEX IX_FormRuleAction_FormRuleId ON meta.FormRuleAction (FormRuleId);
END
GO
