IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('meta.AppTable') AND name = 'Icon')
BEGIN
    ALTER TABLE meta.AppTable ADD Icon NVARCHAR(100) NULL;
END
GO
