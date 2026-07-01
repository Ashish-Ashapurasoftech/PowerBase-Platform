-- Add the ReportLink field type to the control DB's core.FieldType reference data.
-- ReportLink: a value-match hyperlink that opens a filtered view of the target table.
--             No physical column; excluded from all projectors (no computation).
-- Id 28 is pinned to match the tenant baseline (tenant/021_add_reportlink_fieldtype.sql)
-- so meta.AppField.FieldTypeId references stay consistent across databases.

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'ReportLink')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (28, 'ReportLink', 'Report Link', 'Relationship', 'NVARCHAR(MAX)', 0, 0, 0, 29, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
