-- Renames the FormulaUrl TypeCode to Formula_Url so it follows the same Formula_{X} naming
-- convention as every other Formula field-type variant (Formula_Text, Formula_Number, ...).
-- URL and Formula URL were originally one merged field type; when they were split apart
-- (046_seed_formulaurl_fieldtype.sql), the new row was seeded under the standalone code
-- 'FormulaUrl' instead of following the established Formula_{X} convention. This corrects that.
--
-- Renaming Code in place (not deleting + re-inserting) deliberately preserves the row's Id, so
-- every existing meta.AppField that already points at this FieldTypeId keeps working with no
-- data migration needed there — TypeCode is always resolved via the FieldTypeId join, never
-- stored redundantly on AppField itself.
IF EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'FormulaUrl')
   AND NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_Url')
BEGIN
    UPDATE core.FieldType SET Code = 'Formula_Url' WHERE Code = 'FormulaUrl';
END
GO

-- Fresh databases that never ran 046 (or ran it after this migration was introduced) won't have
-- either row yet — seed it directly under the correct code.
IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_Url')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_Url', 'Formula URL', 'Formula', 'NVARCHAR(MAX)', 'pi-link');
END
GO
