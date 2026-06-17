-- Tenant DB: add the relationship field types (replicated from control DB).
--   Reference: physical BIGINT foreign-key column on the child table (holds the parent row Id).
--   Lookup / Summary: computed at read time; no physical column.
-- Ids 25/26/27 are pinned with IDENTITY_INSERT to match the control DB
-- (037_add_relationship_fieldtypes.sql) so meta.AppField.FieldTypeId references stay
-- consistent across databases.

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
