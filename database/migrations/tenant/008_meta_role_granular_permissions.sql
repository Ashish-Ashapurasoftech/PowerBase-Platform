-- Granular per-role permissions: table-level, field-level, and record-level row filters.
-- Tenant DB (meta schema). One tenant per DB — no TenantId columns.

-- ── Table-level permissions: one row per (AppRole, AppTable) ──────────────────
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleTablePermission')
BEGIN
    CREATE TABLE meta.AppRoleTablePermission (
        Id                     BIGINT IDENTITY(1,1) NOT NULL,
        AppRoleId              BIGINT       NOT NULL,
        AppTableId             BIGINT       NOT NULL,
        ViewScope              VARCHAR(20)  NOT NULL DEFAULT 'AllRecords',  -- None | OwnRecords | AllRecords
        ModifyScope            VARCHAR(20)  NOT NULL DEFAULT 'None',        -- None | OwnRecords | AllRecords
        CanAdd                 BIT          NOT NULL DEFAULT 0,
        CanDelete              BIT          NOT NULL DEFAULT 0,
        CanSaveSharedReports   BIT          NOT NULL DEFAULT 0,
        CanEditFieldProperties BIT          NOT NULL DEFAULT 0,
        FieldAccessLevel       VARCHAR(20)  NOT NULL DEFAULT 'FullAccess',  -- FullAccess | CustomAccess
        CONSTRAINT PK_AppRoleTablePermission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRoleTablePermission UNIQUE (AppRoleId, AppTableId),
        CONSTRAINT FK_AppRoleTablePermission_AppRole  FOREIGN KEY (AppRoleId)  REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleTablePermission_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRoleTablePermission_AppRoleId ON meta.AppRoleTablePermission(AppRoleId);
END
GO

-- ── Field-level permissions: one row per (AppRole, AppField) ──────────────────
-- Only non-default rows are persisted; absence implies 'Modify' (FullAccess behaviour).
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleFieldPermission')
BEGIN
    CREATE TABLE meta.AppRoleFieldPermission (
        Id         BIGINT IDENTITY(1,1) NOT NULL,
        AppRoleId  BIGINT      NOT NULL,
        AppFieldId BIGINT      NOT NULL,
        Access     VARCHAR(10) NOT NULL DEFAULT 'Modify',  -- View | Modify | None
        CONSTRAINT PK_AppRoleFieldPermission PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRoleFieldPermission UNIQUE (AppRoleId, AppFieldId),
        CONSTRAINT FK_AppRoleFieldPermission_AppRole  FOREIGN KEY (AppRoleId)  REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleFieldPermission_AppField FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRoleFieldPermission_AppRoleId ON meta.AppRoleFieldPermission(AppRoleId);
END
GO

-- ── Record-level row filters: one row per (AppRole, AppTable) ─────────────────
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'AppRoleRecordFilter')
BEGIN
    CREATE TABLE meta.AppRoleRecordFilter (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        AppRoleId   BIGINT        NOT NULL,
        AppTableId  BIGINT        NOT NULL,
        Conjunction VARCHAR(3)    NOT NULL DEFAULT 'AND',   -- AND | OR
        FilterJson  NVARCHAR(MAX) NOT NULL DEFAULT '[]',    -- [{fieldPublicId, operator, value, useCurrentUser}]
        CONSTRAINT PK_AppRoleRecordFilter PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_AppRoleRecordFilter UNIQUE (AppRoleId, AppTableId),
        CONSTRAINT FK_AppRoleRecordFilter_AppRole  FOREIGN KEY (AppRoleId)  REFERENCES meta.AppRole(Id),
        CONSTRAINT FK_AppRoleRecordFilter_AppTable FOREIGN KEY (AppTableId) REFERENCES meta.AppTable(Id)
    );
    CREATE NONCLUSTERED INDEX IX_AppRoleRecordFilter_AppRoleId ON meta.AppRoleRecordFilter(AppRoleId);
END
GO
