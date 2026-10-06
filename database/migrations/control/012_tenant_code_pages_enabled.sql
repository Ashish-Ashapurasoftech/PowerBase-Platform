IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Tenant') AND name = 'CodePagesEnabled')
BEGIN
    ALTER TABLE meta.Tenant ADD CodePagesEnabled BIT NOT NULL DEFAULT 0;
END
GO
