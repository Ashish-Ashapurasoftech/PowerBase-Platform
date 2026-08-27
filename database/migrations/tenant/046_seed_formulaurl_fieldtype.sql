-- Splits the old "Url with a formula variant" into its own field type: FormulaUrl.
-- Computed (see PhysicalNaming.IsComputedTypeCode) — no physical column, evaluated at read
-- time by FormulaProjector exactly like a Formula field. Category = 'Formula' so it groups
-- with Formula_Text/Formula_Number/etc. in both the Add-Field type picker and the Fields
-- grid's category filter, even though its TypeCode is a standalone 'FormulaUrl', not a
-- 'Formula_*' variant (see FormulaTypeMap.cs for why).
IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'FormulaUrl')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('FormulaUrl', 'Formula URL', 'Formula', 'NVARCHAR(MAX)', 'pi-link');
END
GO
