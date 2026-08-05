-- ============================================================
-- Migration: Group Management
-- Tables  : meta.[Group], meta.GroupMember, meta.GroupApp
-- Indexes : All required indexes
-- Note    : AppUser entry (Source/SourceGroupId) is PENDING
--           and will be handled in a separate migration later.
-- ============================================================


-- ─────────────────────────────────────────────────────────────
-- 1. meta.[Group]
--    Stores named groups. Each group can optionally have a
--    default AppRole assigned for app-sharing.
-- ─────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'Group'
)
BEGIN
    CREATE TABLE meta.[Group] (
        Id          BIGINT IDENTITY(1,1)  NOT NULL,
        PublicId    UNIQUEIDENTIFIER      NOT NULL DEFAULT NEWSEQUENTIALID(),
        Name        NVARCHAR(100)         NOT NULL,
        Description NVARCHAR(500)         NULL,
        AppRoleId   BIGINT                NULL,        -- default role for app sharing
        CreatedOn   DATETIME2(3)          NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy   BIGINT                NOT NULL,
        ModifiedOn  DATETIME2(3)          NULL,
        ModifiedBy  BIGINT                NULL,
        IsDeleted   BIT                   NOT NULL DEFAULT 0,
        DeletedOn   DATETIME2(3)          NULL,
        DeletedBy   BIGINT                NULL,
        RowVersion  ROWVERSION            NOT NULL,
        CONSTRAINT PK_Group         PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_Group_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_Group_AppRole  FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );

    CREATE NONCLUSTERED INDEX IX_Group_Name
        ON meta.[Group](Name)
        WHERE IsDeleted = 0;

    PRINT 'Table meta.[Group] created.';
END
ELSE
    PRINT 'Table meta.[Group] already exists — skipping.';
GO


-- ─────────────────────────────────────────────────────────────
-- 2. meta.GroupMember
--    Maps platform Users (UserId) to a Group.
--    UserId references core.User.Id (platform-level user, not AppUser).
-- ─────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'GroupMember'
)
BEGIN
    CREATE TABLE meta.GroupMember (
        Id        BIGINT IDENTITY(1,1)  NOT NULL,
        GroupId   BIGINT                NOT NULL,
        UserId    BIGINT                NOT NULL,   -- platform UserId (core.User.Id)
        AddedBy   BIGINT                NOT NULL,
        CreatedOn DATETIME2(3)          NOT NULL DEFAULT SYSUTCDATETIME(),
        IsDeleted BIT                   NOT NULL DEFAULT 0,
        CONSTRAINT PK_GroupMember       PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_GroupMember_Group FOREIGN KEY (GroupId) REFERENCES meta.[Group](Id)
    );

    CREATE UNIQUE NONCLUSTERED INDEX UX_GroupMember_Group_User
        ON meta.GroupMember(GroupId, UserId)
        WHERE IsDeleted = 0;

    PRINT 'Table meta.GroupMember created.';
END
ELSE
    PRINT 'Table meta.GroupMember already exists — skipping.';
GO


-- ─────────────────────────────────────────────────────────────
-- 3. meta.GroupApp
--    Links a Group to an App with an assigned AppRole.
--    When a group is shared with an app, all group members
--    inherit the specified role's permissions for that app.
-- ─────────────────────────────────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'GroupApp'
)
BEGIN
    CREATE TABLE meta.GroupApp (
        Id        BIGINT IDENTITY(1,1)  NOT NULL,
        GroupId   BIGINT                NOT NULL,
        AppId     BIGINT                NOT NULL,
        AppRoleId BIGINT                NOT NULL,
        CreatedOn DATETIME2(3)          NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy BIGINT                NOT NULL,
        IsDeleted BIT                   NOT NULL DEFAULT 0,
        CONSTRAINT PK_GroupApp      PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_GroupApp_Group FOREIGN KEY (GroupId)   REFERENCES meta.[Group](Id),
        CONSTRAINT FK_GroupApp_App   FOREIGN KEY (AppId)     REFERENCES meta.App(Id),
        CONSTRAINT FK_GroupApp_Role  FOREIGN KEY (AppRoleId) REFERENCES meta.AppRole(Id)
    );

    CREATE UNIQUE NONCLUSTERED INDEX UX_GroupApp_Group_App
        ON meta.GroupApp(GroupId, AppId)
        WHERE IsDeleted = 0;

    PRINT 'Table meta.GroupApp created.';
END
ELSE
    PRINT 'Table meta.GroupApp already exists — skipping.';
GO


PRINT '=== Group Management Migration — COMPLETE ===';
GO
