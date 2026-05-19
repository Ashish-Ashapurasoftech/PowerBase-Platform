-- Add permissions that were missing from the initial seed
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'tables:update')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('tables:update',  'Update Tables',         'Modify table settings (name, labels, description, icon)');
END
GO

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'fields:update')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('fields:update',  'Update Fields',         'Modify field label, description, and required flag');
END
GO

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'reports:update')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('reports:update', 'Update Reports',        'Modify report name, visibility, columns, and sort');
END
GO

IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'reports:delete')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description) VALUES
        ('reports:delete', 'Delete Reports',        'Soft-delete report definitions');
END
GO
