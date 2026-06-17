-- Add the relationship field types to the control DB's core.FieldType reference data.
--   Reference: a physical BIGINT foreign-key column on the child table (holds the parent row Id).
--   Lookup / Summary: computed at read time by the relationship projector; NO physical column,
--                     so their SqlDataType is a placeholder that is never used by the schema engine.
-- Ids 25/26/27 are pinned to match the tenant baseline (tenant/018_add_relationship_fieldtypes.sql)
-- so meta.AppField.FieldTypeId references stay consistent across databases.

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Reference')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        (25, 'Reference', 'Reference', 'Relationship', 'BIGINT',        0, 1, 0, 26, 1),
        (26, 'Lookup',    'Lookup',    'Relationship', 'NVARCHAR(MAX)', 0, 0, 0, 27, 1),
        (27, 'Summary',   'Summary',   'Relationship', 'NVARCHAR(MAX)', 0, 0, 0, 28, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
