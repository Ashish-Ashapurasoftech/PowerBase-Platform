-- Tenant DB baseline: FieldType reference data

IF NOT EXISTS
(
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'core'
      AND t.name = 'FieldType'
)
BEGIN
    CREATE TABLE core.FieldType
    (
        Id           INT IDENTITY(1,1) NOT NULL,
        Code         VARCHAR(50) NOT NULL,
        DisplayName  NVARCHAR(100) NOT NULL,
        Category     VARCHAR(50) NOT NULL,
        SqlDataType  VARCHAR(50) NOT NULL,
        Icon         VARCHAR(50) NOT NULL,

        CONSTRAINT PK_FieldType PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UX_FieldType_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Code = 'Text')
BEGIN
    SET IDENTITY_INSERT core.FieldType ON;

    INSERT INTO core.FieldType
    (
        Id,
        Code,
        DisplayName,
        Category,
        SqlDataType,
        Icon
    )
    VALUES
        -- Text
        ( 1, 'Text',                    'Text',                    'Text',         'NVARCHAR(MAX)', 'pi-align-left'),
        ( 2, 'TextMultiLine',           'Text - Multiline',        'Text',         'NVARCHAR(MAX)', 'pi-align-justify'),
        ( 3, 'RichText',                'Rich Text',               'Text',         'NVARCHAR(MAX)', 'pi-pencil'),
        ( 4, 'SingleSelect',            'Text - Single Choice',    'Text',         'NVARCHAR(MAX)', 'pi-list'),
        ( 5, 'MultiSelect',             'Text - Multiple Choice',  'Text',         'NVARCHAR(MAX)', 'pi-list-check'),

        -- Numeric
        ( 6, 'Number',                  'Number',                  'Numeric',      'DECIMAL(18,4)', 'pi-hashtag'),
        ( 7, 'Currency',                'Currency',                'Numeric',      'DECIMAL(18,4)', 'pi-dollar'),
        ( 8, 'Percent',                 'Percent',                 'Numeric',      'DECIMAL(18,4)', 'pi-percentage'),
        ( 9, 'Rating',                  'Rating',                  'Numeric',      'INT',           'pi-star'),

        -- Date
        (10, 'Date',                    'Date',                    'Date',         'DATE',          'pi-calendar'),
        (11, 'DateTime',                'Date & Time',             'Date',         'DATETIME2(3)', 'pi-clock'),
        (12, 'Time',                    'Time of Day',             'Date',         'TIME',          'pi-clock'),
        (13, 'Duration',                'Duration',                'Date',         'INT',           'pi-stopwatch'),
        (14, 'DateRange',               'Date Range',              'Date',         'DATE',          'pi-calendar-plus'),

        -- Numeric Range
        (15, 'NumericRange',            'Numeric Range',           'Numeric',      'DECIMAL(18,4)', 'pi-arrows-h'),

        -- Other
        (16, 'Boolean',                 'Checkbox',                'Other',        'BIT',           'pi-check-square'),
        (17, 'Email',                   'Email',                   'Other',        'NVARCHAR(MAX)', 'pi-envelope'),
        (18, 'Phone',                   'Phone',                   'Other',        'NVARCHAR(MAX)', 'pi-phone'),
        (19, 'Url',                     'URL',                     'Other',        'NVARCHAR(MAX)', 'pi-link'),
        (20, 'File',                    'File Attachment',         'Other',        'NVARCHAR(MAX)', 'pi-paperclip'),
        (21, 'Address',                 'Address',                 'Other',        'NVARCHAR(MAX)', 'pi-map-marker'),

        -- User
        (22, 'User',                    'User',                    'User',         'NVARCHAR(MAX)', 'pi-user'),
        (23, 'MultiUser',               'List of Users',           'User',         'NVARCHAR(MAX)', 'pi-users'),

        -- Formula
        (24, 'Formula_Text',            'Formula - text',          'Formula',      'NVARCHAR(MAX)', 'pi-align-left'),
        (25, 'Formula_Number',          'Formula - numeric',       'Formula',      'NVARCHAR(MAX)', 'pi-hashtag'),
        (26, 'Formula_Date',            'Formula - date',          'Formula',      'NVARCHAR(MAX)', 'pi-calendar'),
        (27, 'Formula_DateTime',        'Formula - date / time',   'Formula',      'NVARCHAR(MAX)', 'pi-clock'),
        (28, 'Formula_Duration',        'Formula - duration',      'Formula',      'NVARCHAR(MAX)', 'pi-stopwatch'),
        (29, 'Formula_Bool',            'Formula - checkbox',      'Formula',      'NVARCHAR(MAX)', 'pi-check-square'),
        (30, 'Formula_User',            'Formula - user',          'Formula',      'NVARCHAR(MAX)', 'pi-user'),

        -- Relationship
        (31, 'Reference',               'Reference',               'Relationship', 'BIGINT',        'pi-link'),
        (32, 'Lookup',                  'Lookup',                  'Relationship', 'NVARCHAR(MAX)', 'pi-search'),
        (33, 'Summary',                 'Summary',                 'Relationship', 'NVARCHAR(MAX)', 'pi-chart-bar'),
        (34, 'ReportLink',              'Report Link',             'Relationship', 'NVARCHAR(MAX)', 'pi-external-link'),

        -- Action
        (35, 'ActionButton_File',       'File',                    'Action',       'NVARCHAR(MAX)', 'pi-paperclip'),
        (36, 'ActionButton_Signature',  'Signature',               'Action',       'NVARCHAR(MAX)', 'pi-pencil'),
        (37, 'ActionButton_Prompt',     'Prompt',                  'Action',       'NVARCHAR(MAX)', 'pi-comment'),
        (38, 'ActionButton_Data',       'Data',                    'Action',       'NVARCHAR(MAX)', 'pi-database');

    SET IDENTITY_INSERT core.FieldType OFF;
END
GO