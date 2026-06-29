-- Seed: records:stream (activity stream on View Record page)
--        audit:read     (Application Audit log page in App Settings)
IF NOT EXISTS (SELECT 1 FROM meta.Permission WHERE Code = 'records:stream')
BEGIN
    INSERT INTO meta.Permission (Code, DisplayName, Description)
    VALUES
        ('records:stream', 'View Record Activity Stream', 'Can see the Stream panel on the View Record page — shows who changed which fields and when'),
        ('audit:read',     'View Application Audit Log',  'Can access the Application Audit page to see all app-level activity logs');
END
GO
