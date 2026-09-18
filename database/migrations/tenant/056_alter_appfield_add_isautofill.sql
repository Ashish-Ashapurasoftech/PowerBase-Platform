IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'IsAutoFill')
BEGIN
    ALTER TABLE meta.AppField ADD IsAutoFill BIT NOT NULL DEFAULT 1;
END
GO
