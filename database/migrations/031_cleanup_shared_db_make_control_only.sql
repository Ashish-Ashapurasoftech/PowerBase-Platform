-- Phase 3 (simplified): shared DB becomes control-plane only.
-- All tenant-workspace tables are dropped; each tenant has its own isolated DB.
-- Control tables kept: core.[User], core.SystemRole, core.FieldType,
--   audit.LoginAttempt, audit.UserSession, audit.InviteToken,
--   meta.Tenant, meta.TenantUser, meta.TenantRole, meta.Permission, meta.RolePermission.

-- ── break circular FK: meta.App.DefaultAppRoleId → meta.AppRole ──────────────
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_App_DefaultAppRole')
    ALTER TABLE meta.App DROP CONSTRAINT FK_App_DefaultAppRole;
GO

-- ── form sub-tables ──────────────────────────────────────────────────────────
IF OBJECT_ID('meta.AppRoleTableFormOverride', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleTableFormOverride;
GO

IF OBJECT_ID('meta.FormRuleAction', 'U') IS NOT NULL
    DROP TABLE meta.FormRuleAction;
GO

IF OBJECT_ID('meta.FormRuleCondition', 'U') IS NOT NULL
    DROP TABLE meta.FormRuleCondition;
GO

IF OBJECT_ID('meta.FormRule', 'U') IS NOT NULL
    DROP TABLE meta.FormRule;
GO

IF OBJECT_ID('meta.FormElement', 'U') IS NOT NULL
    DROP TABLE meta.FormElement;
GO

IF OBJECT_ID('meta.FormSectionBlock', 'U') IS NOT NULL
    DROP TABLE meta.FormSectionBlock;
GO

IF OBJECT_ID('meta.FormSection', 'U') IS NOT NULL
    DROP TABLE meta.FormSection;
GO

-- ── report (must come before meta.Form due to FK_Report_ViewEditForm) ────────
IF OBJECT_ID('meta.AppRoleReport', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleReport;
GO

IF OBJECT_ID('meta.Report', 'U') IS NOT NULL
    DROP TABLE meta.Report;
GO

IF OBJECT_ID('meta.Form', 'U') IS NOT NULL
    DROP TABLE meta.Form;
GO

-- ── leaf tables (no dependents) ──────────────────────────────────────────────
IF OBJECT_ID('meta.AppRolePermission', 'U') IS NOT NULL
    DROP TABLE meta.AppRolePermission;
GO

IF OBJECT_ID('meta.AppUser', 'U') IS NOT NULL
    DROP TABLE meta.AppUser;
GO

IF OBJECT_ID('meta.AppVariable', 'U') IS NOT NULL
    DROP TABLE meta.AppVariable;
GO

IF OBJECT_ID('meta.AppField', 'U') IS NOT NULL
    DROP TABLE meta.AppField;
GO

-- ── AppTable (AppField dropped above; Form/FormElement already dropped) ───────
IF OBJECT_ID('meta.AppTable', 'U') IS NOT NULL
    DROP TABLE meta.AppTable;
GO

-- ── AppRole (AppUser/AppRolePermission dropped above) ────────────────────────
IF OBJECT_ID('meta.AppRole', 'U') IS NOT NULL
    DROP TABLE meta.AppRole;
GO

-- ── App (AppTable/AppRole/AppUser/AppVariable all dropped above) ──────────────
IF OBJECT_ID('meta.App', 'U') IS NOT NULL
    DROP TABLE meta.App;
GO

-- ── activity log (tenant-scoped; auth-audit tables stay) ─────────────────────
IF OBJECT_ID('audit.ActivityLog', 'U') IS NOT NULL
    DROP TABLE audit.ActivityLog;
GO

-- ── data schema: drop all data.t_* record tables then the schema ─────────────
DECLARE @sql NVARCHAR(MAX) = '';
SELECT @sql += 'DROP TABLE [data].[' + t.name + '];' + CHAR(13)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'data';

IF LEN(@sql) > 0
    EXEC sp_executesql @sql;
GO

IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'data')
    DROP SCHEMA [data];
GO
