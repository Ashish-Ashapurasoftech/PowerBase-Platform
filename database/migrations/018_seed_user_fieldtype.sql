IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'User')
INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
VALUES ('User', 'User', 'System', 'BIGINT', 0, 0, 0, 10, 1);
