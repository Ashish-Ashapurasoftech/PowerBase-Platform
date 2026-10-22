-- Custom Rules for the Modify scope: meta.AppRoleRecordFilter now holds two rule sets per (role, table):
-- Kind = 'View'   - which records the role can see (existing rows; behaviour unchanged)
-- Kind = 'Modify' - which records the role can edit / delete
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppRoleRecordFilter') AND name = 'Kind')
BEGIN
    ALTER TABLE meta.AppRoleRecordFilter ADD Kind VARCHAR(10) NOT NULL CONSTRAINT DF_AppRoleRecordFilter_Kind DEFAULT('View');
END
GO

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_AppRoleRecordFilter' AND parent_object_id = OBJECT_ID('meta.AppRoleRecordFilter'))
BEGIN
    ALTER TABLE meta.AppRoleRecordFilter DROP CONSTRAINT UX_AppRoleRecordFilter;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UX_AppRoleRecordFilter_Kind' AND parent_object_id = OBJECT_ID('meta.AppRoleRecordFilter'))
BEGIN
    ALTER TABLE meta.AppRoleRecordFilter ADD CONSTRAINT UX_AppRoleRecordFilter_Kind UNIQUE (AppRoleId, AppTableId, Kind);
END
GO
