-- Pages feature — shared layer (Phase 1).
-- meta.Page: app-scoped list entry for both Dashboard and Code page types.
-- meta.PageVersion: append-only version history with a mandatory change note.
-- meta.AppRolePage: per-role view visibility, mirroring meta.AppRoleReport.

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Page')
BEGIN
    CREATE TABLE meta.Page (
        Id                 BIGINT IDENTITY(1,1) NOT NULL,
        PublicId           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        AppId              BIGINT        NOT NULL,
        PageNumber         INT           NOT NULL,          -- per-app friendly id, never recycled
        PageType           VARCHAR(20)   NOT NULL,          -- 'Dashboard' | 'Code'
        Name               NVARCHAR(200) NOT NULL,
        Description        NVARCHAR(500) NULL,
        OwnerId            BIGINT        NOT NULL,
        Visibility         VARCHAR(20)   NOT NULL DEFAULT 'Personal',  -- Personal|MyRole|SpecificRoles|Shared
        Definition         NVARCHAR(MAX) NOT NULL DEFAULT '{}',        -- Dashboard config JSON
        ContentType        VARCHAR(10)   NULL,               -- Code only: 'html' | 'css' | 'js' (primary asset marker)
        CodeHtml           NVARCHAR(MAX) NULL,
        CodeCss            NVARCHAR(MAX) NULL,
        CodeJs             NVARCHAR(MAX) NULL,
        IsPublished        BIT           NOT NULL DEFAULT 0,
        CurrentVersionNo   INT           NOT NULL DEFAULT 1,
        PublishedVersionNo INT           NULL,
        ShowInNav          BIT           NOT NULL DEFAULT 0,
        NavOrder           INT           NOT NULL DEFAULT 0,
        NavIcon            NVARCHAR(50)  NULL,
        IsDefaultHome      BIT           NOT NULL DEFAULT 0,
        IsDeleted          BIT           NOT NULL DEFAULT 0,
        CreatedOn          DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy          BIGINT        NOT NULL DEFAULT 0,
        ModifiedOn         DATETIME2(3)  NULL,
        ModifiedBy         BIGINT        NULL,
        DeletedOn          DATETIME2(3)  NULL,
        DeletedBy          BIGINT        NULL,
        RowVersion         ROWVERSION    NOT NULL,
        CONSTRAINT PK_Page PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Page_PublicId UNIQUE (PublicId),
        CONSTRAINT CK_Page_Type CHECK (PageType IN ('Dashboard','Code')),
        CONSTRAINT CK_Page_Visibility CHECK (Visibility IN ('Personal','Shared','MyRole','SpecificRoles')),
        CONSTRAINT FK_Page_App FOREIGN KEY (AppId) REFERENCES meta.App(Id)
    );
    -- Unfiltered: a deleted page must never have its PageNumber recycled, or an old
    -- bookmarked friendly URL (/p/{appId}/{pageNumber}) could resolve to a different page later.
    CREATE UNIQUE NONCLUSTERED INDEX UX_Page_App_Number ON meta.Page(AppId, PageNumber);
    CREATE UNIQUE NONCLUSTERED INDEX UX_Page_App_Name_Active ON meta.Page(AppId, Name) WHERE IsDeleted = 0;
    CREATE NONCLUSTERED INDEX IX_Page_App_Active ON meta.Page(AppId) WHERE IsDeleted = 0;
    -- At most one default-home page per app.
    CREATE UNIQUE NONCLUSTERED INDEX UX_Page_App_DefaultHome ON meta.Page(AppId) WHERE IsDefaultHome = 1 AND IsDeleted = 0;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'PageVersion')
BEGIN
    CREATE TABLE meta.PageVersion (
        Id           BIGINT IDENTITY(1,1) NOT NULL,
        PublicId     UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
        PageId       BIGINT        NOT NULL,
        VersionNo    INT           NOT NULL,
        PageType     VARCHAR(20)   NOT NULL,
        Name         NVARCHAR(200) NOT NULL,
        Description  NVARCHAR(500) NULL,
        Definition   NVARCHAR(MAX) NOT NULL DEFAULT '{}',
        CodeHtml     NVARCHAR(MAX) NULL,
        CodeCss      NVARCHAR(MAX) NULL,
        CodeJs       NVARCHAR(MAX) NULL,
        ChangeNotes  NVARCHAR(1000) NOT NULL,
        WasPublished BIT           NOT NULL DEFAULT 0,
        CreatedOn    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy    BIGINT        NOT NULL DEFAULT 0,
        CONSTRAINT PK_PageVersion PRIMARY KEY CLUSTERED (PageId, VersionNo),
        CONSTRAINT UX_PageVersion_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_PageVersion_Page FOREIGN KEY (PageId) REFERENCES meta.Page(Id)
    );
END
GO

-- Append-only: no application code may UPDATE or DELETE a version row. This is enforced
-- at the DB level (not just "by convention") so a future careless handler can't violate it.
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'TR_PageVersion_AppendOnly')
BEGIN
    EXEC('
        CREATE TRIGGER meta.TR_PageVersion_AppendOnly ON meta.PageVersion
        INSTEAD OF UPDATE, DELETE
        AS BEGIN
            THROW 51000, ''meta.PageVersion is append-only — versions cannot be updated or deleted.'', 1;
        END
    ');
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRolePage')
BEGIN
    CREATE TABLE meta.AppRolePage (
        PageId    BIGINT NOT NULL,
        AppRoleId BIGINT NOT NULL,
        CONSTRAINT PK_AppRolePage PRIMARY KEY CLUSTERED (PageId, AppRoleId),
        CONSTRAINT FK_AppRolePage_Page    FOREIGN KEY (PageId)    REFERENCES meta.Page(Id),
        CONSTRAINT FK_AppRolePage_AppRole FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRolePage_Role ON meta.AppRolePage(AppRoleId);
END
GO

-- Per-role home page ("Home page for" in the Pages list). One page per role, so it lives
-- on the role, not on a join table.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRole') AND name = 'HomePageId')
BEGIN
    ALTER TABLE meta.AppRole ADD HomePageId BIGINT NULL
        CONSTRAINT FK_AppRole_HomePage FOREIGN KEY REFERENCES meta.Page(Id);
END
GO
