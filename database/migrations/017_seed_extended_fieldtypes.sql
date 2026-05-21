IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'DateTime')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('DateTime', 'Date & Time', 'Basic', 'DATETIME2(3)', 1, 1, 0, 5, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Email')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Email', 'Email Address', 'Contact', 'NVARCHAR(500)', 1, 1, 1, 6, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Phone')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Phone', 'Phone Number', 'Contact', 'NVARCHAR(50)', 1, 1, 0, 7, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Url')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Url', 'URL', 'Contact', 'NVARCHAR(500)', 1, 1, 0, 8, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Duration')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Duration', 'Duration', 'Basic', 'INT', 1, 0, 0, 9, 1);
GO
