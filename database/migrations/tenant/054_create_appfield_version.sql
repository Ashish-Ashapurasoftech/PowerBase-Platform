-- Field versioning / audit history (see PowerBase.Application.Fields.Versioning). Every successful
-- field-settings update or restore inserts one meta.AppFieldVersion row (append-only — never
-- updated or deleted) plus one meta.AppFieldVersionChange row per changed property, so the Field
-- Detail page's Audit History tab can show a structured before/after per version and restore any
-- prior version without ever rewriting history.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AppFieldVersion' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.AppFieldVersion (
        Id                  BIGINT IDENTITY PRIMARY KEY,
        PublicId            UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AppFieldVersion_PublicId DEFAULT NEWID(),
        AppFieldId          BIGINT NOT NULL,
        Version             INT NOT NULL,
        -- 1 = Update (a normal Field Detail save), 2 = Restore (re-applying a prior version).
        ChangeType          TINYINT NOT NULL,
        -- The version number this row restored from. NULL for a plain Update.
        RestoredFromVersion INT NULL,
        CommitMessage       NVARCHAR(500) NOT NULL,
        -- Platform-level user id (IQueryContext.UserId) — NOT an FK. core.[User] is a control-plane
        -- table and does not exist in a tenant database (same reason meta.AppUser stores UserName/
        -- UserEmail denormalized instead of joining a per-tenant Users table — see
        -- database/migrations/tenant/003_meta_tenant_tables.sql). ChangedByName below is that same
        -- denormalization, captured at write time so the Audit History grid never needs a join.
        ChangedByUserId     BIGINT NOT NULL,
        ChangedByName       NVARCHAR(256) NOT NULL,
        ChangedOn           DATETIME2 NOT NULL CONSTRAINT DF_AppFieldVersion_ChangedOn DEFAULT SYSUTCDATETIME(),
        -- Full field-settings snapshot as of this version (see FieldSnapshot) — lets a restore read
        -- the target configuration directly instead of replaying every diff since that version.
        SnapshotJson        NVARCHAR(MAX) NOT NULL,

        CONSTRAINT FK_AppFieldVersion_AppField FOREIGN KEY (AppFieldId) REFERENCES meta.AppField(Id),
        CONSTRAINT UQ_AppFieldVersion_Field_Version UNIQUE (AppFieldId, Version)
    );

    CREATE INDEX IX_AppFieldVersion_Field ON meta.AppFieldVersion(AppFieldId, Version DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AppFieldVersionChange' AND schema_id = SCHEMA_ID('meta'))
BEGIN
    CREATE TABLE meta.AppFieldVersionChange (
        Id                BIGINT IDENTITY PRIMARY KEY,
        AppFieldVersionId BIGINT NOT NULL,
        -- Dotted path for a nested Settings property, e.g. "IsRequired" or "Settings.MaxLength" —
        -- see FieldSnapshotDiffer. Structured (not a free-text description) so the Audit History
        -- detail view can render a Setting / Previous / New table per requirement.
        PropertyName      NVARCHAR(100) NOT NULL,
        OldValue          NVARCHAR(MAX) NULL,
        NewValue          NVARCHAR(MAX) NULL,

        CONSTRAINT FK_AppFieldVersionChange_Version FOREIGN KEY (AppFieldVersionId)
            REFERENCES meta.AppFieldVersion(Id)
    );

    CREATE INDEX IX_AppFieldVersionChange_Version ON meta.AppFieldVersionChange(AppFieldVersionId);
END
GO
