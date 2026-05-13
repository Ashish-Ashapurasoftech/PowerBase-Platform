IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Text')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Text', 'Text', 'Basic', 'NVARCHAR(500)', 1, 1, 1, 1, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Number')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Number', 'Number', 'Basic', 'DECIMAL(18,4)', 1, 1, 1, 2, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Date')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Date', 'Date', 'Basic', 'DATE', 1, 1, 0, 3, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Boolean')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Boolean', 'Boolean (Yes/No)', 'Basic', 'BIT', 1, 0, 0, 4, 1);
GO
