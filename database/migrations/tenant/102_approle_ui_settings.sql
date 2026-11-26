-- Migration 102: meta.AppRole.UiSettingsJson — the role's "User interface" settings (Roles > User interface tab):
-- which tables / buttons / menus / personal-settings sections are hidden for users of the role.
-- UI-only: it hides things in the client and never grants or removes data access (that stays in the
-- Data Permissions tables). NULL = nothing hidden.
IF COL_LENGTH('meta.AppRole', 'UiSettingsJson') IS NULL
    ALTER TABLE meta.AppRole ADD UiSettingsJson NVARCHAR(MAX) NULL;
GO
