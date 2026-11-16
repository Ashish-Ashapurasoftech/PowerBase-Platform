-- Migration 095: meta.AppFieldImportNote - field-level notes written by a QBL/PBL import.
-- One row per warning/information about one imported field: why a field is only a placeholder, what could not be
-- carried over, or that a formula was rewritten. The complete original formula / definition and the source field's
-- metadata are kept here (NVARCHAR(MAX), never truncated), separate from the field's own Description and HelpText,
-- which stay the end user's text. Additive only: no existing table or column is altered.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppFieldImportNote')
BEGIN
    CREATE TABLE meta.AppFieldImportNote (
        Id                 BIGINT IDENTITY(1,1) NOT NULL,
        PublicId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppFieldImportNote_PublicId DEFAULT NEWID(),
        AppId              BIGINT        NOT NULL,
        AppTableId         BIGINT        NOT NULL,
        AppFieldId         BIGINT        NOT NULL,
        Severity           NVARCHAR(16)  NOT NULL,                  -- Information | Warning | Error
        Code               NVARCHAR(100) NOT NULL CONSTRAINT DF_AppFieldImportNote_Code DEFAULT '',
        Message            NVARCHAR(MAX) NOT NULL,                  -- what happened, in plain words
        RecommendedAction  NVARCHAR(1000) NULL,
        SourceLogicalRef   NVARCHAR(300) NULL,                      -- the QBL/PBL ref the field came from
        SourceType         NVARCHAR(200) NULL,                      -- e.g. QB::Field::Summary
        SourceFieldId      INT           NULL,                      -- the source field's own number (the Fid it was given)
        IsPlaceholder      BIT           NOT NULL CONSTRAINT DF_AppFieldImportNote_IsPlaceholder DEFAULT 0,
        OriginalDefinition NVARCHAR(MAX) NULL,                      -- the complete original formula / summary / lookup definition
        OriginalMetadata   NVARCHAR(MAX) NULL,                      -- the source field's properties, as JSON
        CreatedOn          DATETIME2(3)  NOT NULL CONSTRAINT DF_AppFieldImportNote_CreatedOn DEFAULT SYSUTCDATETIME(),
        ResolvedOn         DATETIME2(3)  NULL,                      -- set when a person repairs the field and dismisses the note
        ResolvedBy         BIGINT        NULL,
        CONSTRAINT PK_AppFieldImportNote PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_AppFieldImportNote_App FOREIGN KEY (AppId) REFERENCES meta.App(Id),
        CONSTRAINT FK_AppFieldImportNote_Field FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id),
        CONSTRAINT CK_AppFieldImportNote_Severity CHECK (Severity IN ('Information', 'Warning', 'Error'))
    );
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppFieldImportNote_PublicId ON meta.AppFieldImportNote (PublicId);
    -- "Which fields of this table carry a note?" (the fields grid) and "the notes of this field" (the field page).
    CREATE NONCLUSTERED INDEX IX_AppFieldImportNote_Table ON meta.AppFieldImportNote (AppTableId) INCLUDE (AppFieldId, Severity, IsPlaceholder, ResolvedOn);
    CREATE NONCLUSTERED INDEX IX_AppFieldImportNote_Field ON meta.AppFieldImportNote (AppFieldId) INCLUDE (Severity, ResolvedOn);
END
GO
