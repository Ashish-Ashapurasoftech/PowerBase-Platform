-- Tenant DB baseline: FieldType reference data (replicated from control DB)
-- IDs are pinned with IDENTITY_INSERT so they match the shared DB exactly.
-- meta.AppField has FK_AppField_FieldType → core.FieldType(Id).

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'core' AND t.name = 'FieldType')
BEGIN
    CREATE TABLE core.FieldType (
        Id               INT IDENTITY(1,1) NOT NULL,
        Code             VARCHAR(50)   NOT NULL,
        DisplayName      NVARCHAR(100) NOT NULL,
        Category         VARCHAR(50)   NULL,
        SqlDataType      VARCHAR(50)   NOT NULL,
        SupportsDefault  BIT           NOT NULL DEFAULT 1,
        SupportsRequired BIT           NOT NULL DEFAULT 1,
        SupportsUnique   BIT           NOT NULL DEFAULT 0,
        DisplayOrder     INT           NOT NULL DEFAULT 0,
        IsActive         BIT           NOT NULL DEFAULT 1,
        CONSTRAINT PK_FieldType PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FieldType_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Text')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;
    INSERT INTO core.FieldType (Id, Code, DisplayName, Category, SqlDataType, SupportsDefault, SupportsRequired, SupportsUnique, DisplayOrder, IsActive) VALUES
        ( 1, 'Text',          'Text',              'Text',    'NVARCHAR(500)',  1, 1, 1,  1, 1),
        ( 2, 'Number',        'Number',            'Numeric', 'DECIMAL(18,4)', 1, 1, 1,  2, 1),
        ( 3, 'Date',          'Date',              'Date',    'DATE',          1, 1, 0,  3, 1),
        ( 4, 'Boolean',       'Checkbox',          'Other',   'BIT',           1, 0, 0,  4, 1),
        ( 5, 'TextMultiLine', 'Text - Multiline',  'Text',    'NVARCHAR(MAX)', 1, 1, 0,  2, 1),
        ( 6, 'RichText',      'Rich Text',         'Text',    'NVARCHAR(MAX)', 0, 1, 0,  3, 1),
        ( 7, 'SingleSelect',  'Single Select',     'Text',    'NVARCHAR(500)', 1, 1, 0,  4, 1),
        ( 8, 'MultiSelect',   'Multi Select',      'Text',    'NVARCHAR(MAX)', 1, 1, 0,  5, 1),
        ( 9, 'Currency',      'Currency',          'Numeric', 'DECIMAL(18,4)', 1, 1, 0,  7, 1),
        (10, 'Percent',       'Percent',           'Numeric', 'DECIMAL(18,4)', 1, 1, 0,  8, 1),
        (11, 'Rating',        'Rating',            'Numeric', 'INT',           1, 1, 0,  9, 1),
        (12, 'DateTime',      'Date & Time',       'Date',    'DATETIME2(3)',  1, 1, 0, 11, 1),
        (13, 'Time',          'Time of Day',       'Date',    'TIME',          1, 1, 0, 12, 1),
        (14, 'Duration',      'Duration',          'Date',    'INT',           1, 1, 0, 13, 1),
        (15, 'Email',         'Email',             'Other',   'NVARCHAR(500)', 1, 1, 1, 15, 1),
        (16, 'Phone',         'Phone',             'Other',   'NVARCHAR(50)',  1, 1, 1, 16, 1),
        (17, 'Url',           'URL',               'Other',   'NVARCHAR(MAX)', 1, 1, 0, 17, 1),
        (18, 'File',          'File Attachment',   'Other',   'NVARCHAR(MAX)', 0, 1, 0, 18, 1),
        (19, 'Address',       'Address',           'Other',   'NVARCHAR(MAX)', 0, 1, 0, 19, 1),
        (20, 'User',          'User',              'User',    'NVARCHAR(500)', 1, 1, 0, 20, 1),
        (21, 'MultiUser',     'List of Users',     'User',    'NVARCHAR(MAX)', 1, 1, 0, 21, 1);
    SET IDENTITY_INSERT core.FieldType OFF;
END
GO
