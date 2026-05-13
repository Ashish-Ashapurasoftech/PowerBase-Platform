IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Text')
    INSERT INTO core.FieldType (Code, Name, SqlType, IsActive) VALUES ('Text', 'Text', 'NVARCHAR(500)', 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Number')
    INSERT INTO core.FieldType (Code, Name, SqlType, IsActive) VALUES ('Number', 'Number', 'DECIMAL(18,4)', 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Date')
    INSERT INTO core.FieldType (Code, Name, SqlType, IsActive) VALUES ('Date', 'Date', 'DATE', 1);

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Boolean')
    INSERT INTO core.FieldType (Code, Name, SqlType, IsActive) VALUES ('Boolean', 'Boolean', 'BIT', 1);
GO
