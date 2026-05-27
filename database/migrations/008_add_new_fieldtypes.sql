UPDATE core.FieldType SET Category = 'Text' WHERE Code = 'Text';
UPDATE core.FieldType SET Category = 'Numeric' WHERE Code = 'Number';
UPDATE core.FieldType SET Category = 'Date' WHERE Code = 'Date';
UPDATE core.FieldType SET Category = 'Other', DisplayName = 'Checkbox' WHERE Code = 'Boolean';

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'TextMultiLine')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('TextMultiLine', 'Text - Multiline', 'Text', 'NVARCHAR(MAX)', 1, 1, 0, 2, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'RichText')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('RichText', 'Rich Text', 'Text', 'NVARCHAR(MAX)', 0, 1, 0, 3, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'SingleSelect')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('SingleSelect', 'Single Select', 'Text', 'NVARCHAR(500)', 1, 1, 0, 4, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'MultiSelect')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('MultiSelect', 'Multi Select', 'Text', 'NVARCHAR(MAX)', 1, 1, 0, 5, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Currency')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Currency', 'Currency', 'Numeric', 'DECIMAL(18,4)', 1, 1, 0, 7, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Percent')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Percent', 'Percent', 'Numeric', 'DECIMAL(18,4)', 1, 1, 0, 8, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Rating')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Rating', 'Rating', 'Numeric', 'INT', 1, 1, 0, 9, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'DateTime')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('DateTime', 'Date & Time', 'Date', 'DATETIME2', 1, 1, 0, 11, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Time')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Time', 'Time of Day', 'Date', 'TIME', 1, 1, 0, 12, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Duration')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Duration', 'Duration', 'Date', 'INT', 1, 1, 0, 13, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Email')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Email', 'Email', 'Other', 'NVARCHAR(500)', 1, 1, 1, 15, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Phone')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Phone', 'Phone', 'Other', 'NVARCHAR(50)', 1, 1, 1, 16, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Url')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Url', 'URL', 'Other', 'NVARCHAR(MAX)', 1, 1, 0, 17, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'File')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('File', 'File Attachment', 'Other', 'NVARCHAR(MAX)', 0, 1, 0, 18, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Address')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('Address', 'Address', 'Other', 'NVARCHAR(MAX)', 0, 1, 0, 19, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'User')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('User', 'User', 'User', 'BIGINT', 1, 1, 0, 20, 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'MultiUser')
    INSERT INTO core.FieldType (Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive)
    VALUES ('MultiUser', 'List of Users', 'User', 'NVARCHAR(MAX)', 1, 1, 0, 21, 1);
GO
