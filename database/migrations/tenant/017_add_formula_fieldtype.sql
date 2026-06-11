-- Tenant DB: add the Formula field type (replicated from control DB).
-- Computed at read time; no physical storage column. Id 24 is pinned with
-- IDENTITY_INSERT to match the control DB (036_add_formula_fieldtype.sql) so
-- meta.AppField.FieldTypeId references stay consistent across databases.

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (24, 'Formula', 'Formula', 'Formula', 'NVARCHAR(MAX)', 0, 0, 0, 25, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
