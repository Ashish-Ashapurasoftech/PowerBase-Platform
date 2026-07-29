-- Tenant DB: add the ActionButton field type (replicated from control DB).
-- Computed at read time; no physical storage column. Id 29 is pinned with
-- IDENTITY_INSERT to match the control DB (040_add_actionbutton_fieldtype.sql) so
-- meta.AppField.FieldTypeId references stay consistent across databases.

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'ActionButton')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (29, 'ActionButton', 'Action Button', 'Action', 'NVARCHAR(MAX)', 0, 0, 0, 30, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
