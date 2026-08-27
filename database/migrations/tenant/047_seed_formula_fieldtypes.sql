-- Adds the 4 remaining Formula field-type variants (TimeOfDay/Phone/Email/RichText) so every
-- field type in the app has a Formula counterpart. Computed (see PhysicalNaming.IsComputedTypeCode
-- / PhysicalNaming.IsFormulaVariantTypeCode) — no physical column, evaluated at read time by
-- FormulaProjector exactly like Formula_Text/Formula_Number/etc. (seeded in 002_core_fieldtype.sql).
IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_Time')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_Time', 'Formula - time', 'Formula', 'NVARCHAR(MAX)', 'pi-clock');
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_Phone')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_Phone', 'Formula - phone', 'Formula', 'NVARCHAR(MAX)', 'pi-phone');
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_Email')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_Email', 'Formula - email', 'Formula', 'NVARCHAR(MAX)', 'pi-envelope');
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Formula_RichText')
BEGIN
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, Icon)
    VALUES ('Formula_RichText', 'Formula - rich text', 'Formula', 'NVARCHAR(MAX)', 'pi-pencil');
END
GO
