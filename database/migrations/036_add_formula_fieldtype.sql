-- Add the Formula field type to the control DB's core.FieldType reference data.
-- Formula fields are computed at read time and store NO physical column, so the
-- SqlDataType below is a placeholder that is never used by the schema engine.
-- Id 24 is pinned to match the tenant baseline (tenant/017_add_formula_fieldtype.sql).

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (24, 'Formula', 'Formula', 'Formula', 'NVARCHAR(MAX)', 0, 0, 0, 25, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
