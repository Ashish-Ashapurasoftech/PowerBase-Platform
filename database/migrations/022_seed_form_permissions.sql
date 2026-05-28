IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'forms:create')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES
        ('forms:create',       'Create Forms',        'Create and duplicate form definitions'),
        ('forms:read',         'View Forms',          'View form list and form designer'),
        ('forms:update',       'Update Forms',        'Save form layout and settings'),
        ('forms:delete',       'Delete Forms',        'Soft-delete form definitions'),
        ('forms:rules:manage', 'Manage Form Rules',   'Create, edit, reorder, and delete form rules');
END
GO
