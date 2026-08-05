-- Add the ActionButton field type to the control DB's core.FieldType reference data.
-- ActionButton: an interactive field type (Signature | File | Prompt | Data variants).
--               Clicking it writes results back into other fields on the record via the
--               InvokeButtonAction endpoint. No physical column; excluded from all projectors.
-- Id 29 is pinned to match the tenant baseline (tenant/026_add_actionbutton_fieldtype.sql)
-- so meta.AppField.FieldTypeId references stay consistent across databases.

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'ActionButton')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (29, 'ActionButton', 'Action Button', 'Action', 'NVARCHAR(MAX)', 0, 0, 0, 30, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
