-- Adds the two list-valued Formula field-type variants: Formula_ListUser (formula returns a
-- User List) and Formula_MultiSelect (formula returns a Text List). Computed like every other
-- Formula_{X} variant (see PhysicalNaming.IsComputedTypeCode / IsFormulaVariantTypeCode) — no
-- physical column, evaluated at read time by FormulaProjector. Idempotent.
IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_ListUser')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_ListUser', 'Formula - list user', 'Formula', 'NVARCHAR(MAX)', 'pi-users');
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_MultiSelect')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_MultiSelect', 'Formula - multiselect text', 'Formula', 'NVARCHAR(MAX)', 'pi-list-check');
END
GO
