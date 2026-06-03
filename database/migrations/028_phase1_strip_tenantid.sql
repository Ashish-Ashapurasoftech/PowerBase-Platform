-- Phase 1: Make TenantId nullable in all tenant-scoped tables.
-- In Phase 2+ each tenant has its own isolated database so TenantId columns
-- are absent from the clean schema.  While the shared database is still in
-- use we simply stop supplying TenantId values in code and let the columns
-- hold NULL for new rows.  Existing rows retain their old values; they will
-- be cleaned up during the Phase 3 data-move.

-- ── meta.App ────────────────────────────────────────────────────────────────
ALTER TABLE meta.App DROP CONSTRAINT FK_App_Tenant;
GO
DROP INDEX IF EXISTS IX_App_TenantId ON meta.App;
GO
ALTER TABLE meta.App ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppTable ────────────────────────────────────────────────────────────
ALTER TABLE meta.AppTable ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppField ────────────────────────────────────────────────────────────
ALTER TABLE meta.AppField ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppRole ─────────────────────────────────────────────────────────────
DROP INDEX IF EXISTS IX_AppRole_TenantId ON meta.AppRole;
GO
ALTER TABLE meta.AppRole ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppUser ─────────────────────────────────────────────────────────────
DROP INDEX IF EXISTS IX_AppUser_TenantId ON meta.AppUser;
GO
ALTER TABLE meta.AppUser ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppVariable ─────────────────────────────────────────────────────────
ALTER TABLE meta.AppVariable DROP CONSTRAINT FK_AppVariable_Tenant;
GO
DROP INDEX IF EXISTS UX_AppVariable_AppName_Active ON meta.AppVariable;
GO
DROP INDEX IF EXISTS IX_AppVariable_AppId ON meta.AppVariable;
GO
ALTER TABLE meta.AppVariable ALTER COLUMN TenantId BIGINT NULL;
GO
-- Recreate the uniqueness constraint scoped only to AppId + Name (TenantId removed)
CREATE UNIQUE NONCLUSTERED INDEX UX_AppVariable_AppName_Active
    ON meta.AppVariable (AppId, Name)
    WHERE IsDeleted = 0;
GO

-- ── meta.Report ──────────────────────────────────────────────────────────────
DROP INDEX IF EXISTS IX_Report_TenantId ON meta.Report;
GO
ALTER TABLE meta.Report ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.Form ────────────────────────────────────────────────────────────────
DROP INDEX IF EXISTS IX_Form_TenantId ON meta.Form;
GO
ALTER TABLE meta.Form ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.FormSection ─────────────────────────────────────────────────────────
ALTER TABLE meta.FormSection ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.FormElement ─────────────────────────────────────────────────────────
ALTER TABLE meta.FormElement ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.FormRule ────────────────────────────────────────────────────────────
DROP INDEX IF EXISTS IX_FormRule_TenantId ON meta.FormRule;
GO
ALTER TABLE meta.FormRule ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── audit.ActivityLog ────────────────────────────────────────────────────────
ALTER TABLE audit.ActivityLog ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.FormSectionBlock ────────────────────────────────────────────────────
ALTER TABLE meta.FormSectionBlock ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── meta.AppRoleTableFormOverride ────────────────────────────────────────────
DROP INDEX IF EXISTS IX_AppRoleTableFormOverride_TenantId ON meta.AppRoleTableFormOverride;
GO
ALTER TABLE meta.AppRoleTableFormOverride ALTER COLUMN TenantId BIGINT NULL;
GO

-- ── data.t_* (dynamically created record tables) ────────────────────────────
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql += N'ALTER TABLE data.' + QUOTENAME(t.name)
             + N' ALTER COLUMN TenantId BIGINT NULL;' + NCHAR(13)
FROM sys.tables  t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id AND c.name = 'TenantId'
WHERE s.name = 'data' AND t.name LIKE 't[_]%';
IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO
