-- Remembers which QBL/PBL logical ref each imported element came from, so a later "Update Existing App"
-- import can match elements by logical ref (never by DBID) and stays idempotent on retry. One row per
-- (app, element type, source logical ref); ElementId is the internal Id of the created PowerBase element.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'ImportSourceRef')
BEGIN
    CREATE TABLE meta.ImportSourceRef (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        AppId            BIGINT        NOT NULL,
        ElementType      NVARCHAR(30)  NOT NULL,
        ElementId        BIGINT        NOT NULL,
        SourceLogicalRef NVARCHAR(300) NOT NULL,
        CreatedOn        DATETIME2(3)  NOT NULL CONSTRAINT DF_ImportSourceRef_CreatedOn DEFAULT SYSUTCDATETIME(),
        ModifiedOn       DATETIME2(3)  NULL,
        CONSTRAINT PK_ImportSourceRef PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_ImportSourceRef_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    CREATE UNIQUE NONCLUSTERED INDEX UX_ImportSourceRef_App_Type_Ref ON meta.ImportSourceRef (AppId, ElementType, SourceLogicalRef);
    CREATE NONCLUSTERED INDEX IX_ImportSourceRef_App_Type_Element ON meta.ImportSourceRef (AppId, ElementType, ElementId);
END
GO
