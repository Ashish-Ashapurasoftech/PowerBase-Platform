-- PowerFlow descriptions have no length limit (Quickbase pipeline descriptions longer than 500 characters were dropped on import).
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Pipeline') AND name = 'Description' AND max_length <> -1)
BEGIN
    ALTER TABLE meta.Pipeline ALTER COLUMN Description NVARCHAR(MAX) NULL;
END
GO
