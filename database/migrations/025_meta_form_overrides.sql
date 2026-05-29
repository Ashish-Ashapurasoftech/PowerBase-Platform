IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleTableFormOverride')
BEGIN
    CREATE TABLE meta.AppRoleTableFormOverride (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        TenantId     BIGINT NOT NULL,
        AppTableId   BIGINT NOT NULL,
        AppRoleId    BIGINT NULL,
        EditFormId   BIGINT NULL,
        AddFormId    BIGINT NULL,
        CreatedOn    DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy    BIGINT NOT NULL,
        ModifiedOn   DATETIME2(3) NULL,
        ModifiedBy   BIGINT NULL,
        CONSTRAINT PK_AppRoleTableFormOverride PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_EditForm FOREIGN KEY (EditFormId) REFERENCES meta.Form(Id),
        CONSTRAINT FK_AppRoleTableFormOverride_AddForm FOREIGN KEY (AddFormId) REFERENCES meta.Form(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRoleTableFormOverride_TableRole ON meta.AppRoleTableFormOverride(AppTableId, AppRoleId);
    CREATE NONCLUSTERED INDEX IX_AppRoleTableFormOverride_TenantId ON meta.AppRoleTableFormOverride(TenantId);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.tables t ON c.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'meta' AND t.name = 'Report' AND c.name = 'ViewEditFormId'
)
BEGIN
    ALTER TABLE meta.Report ADD ViewEditFormId BIGINT NULL;
    ALTER TABLE meta.Report ADD CONSTRAINT FK_Report_ViewEditForm FOREIGN KEY (ViewEditFormId) REFERENCES meta.Form(Id);
END
GO
