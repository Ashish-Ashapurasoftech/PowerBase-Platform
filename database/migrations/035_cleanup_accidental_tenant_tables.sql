-- Drops the three granular-permission tables that were accidentally created in the
-- control DB when tenant migrations ran against it (via the Phase 0 DatabaseName backfill).
-- These tables reference meta.AppRole/AppField/AppTable, which blocked 031 from completing.
-- After these are removed, the remaining tenant-workspace tables left by the failed 031
-- run are also cleaned up here.

-- ── new granular-permission tables (from tenant/008) ─────────────────────────
IF OBJECT_ID('meta.AppRoleRecordFilter', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleRecordFilter;
GO

IF OBJECT_ID('meta.AppRoleFieldPermission', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleFieldPermission;
GO

IF OBJECT_ID('meta.AppRoleTablePermission', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleTablePermission;
GO

-- ── remaining tables that 031 failed to drop ─────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_App_DefaultAppRole')
    ALTER TABLE meta.App DROP CONSTRAINT FK_App_DefaultAppRole;
GO

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

IF OBJECT_ID('meta.AppRoleReport', 'U') IS NOT NULL
    DROP TABLE meta.AppRoleReport;
GO

IF OBJECT_ID('meta.Report', 'U') IS NOT NULL
    DROP TABLE meta.Report;
GO

IF OBJECT_ID('meta.Form', 'U') IS NOT NULL
    DROP TABLE meta.Form;
GO

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

IF OBJECT_ID('meta.AppTable', 'U') IS NOT NULL
    DROP TABLE meta.AppTable;
GO

IF OBJECT_ID('meta.AppRole', 'U') IS NOT NULL
    DROP TABLE meta.AppRole;
GO

IF OBJECT_ID('meta.App', 'U') IS NOT NULL
    DROP TABLE meta.App;
GO

IF OBJECT_ID('audit.ActivityLog', 'U') IS NOT NULL
    DROP TABLE audit.ActivityLog;
GO

-- ── data schema (record tables) ──────────────────────────────────────────────
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
