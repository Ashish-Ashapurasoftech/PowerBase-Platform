-- Migration 042: Alter UserToken TokenPrefix column to allow full token length
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('core.UserToken') AND name = 'TokenPrefix' AND max_length < 100)
BEGIN
    ALTER TABLE core.UserToken ALTER COLUMN TokenPrefix NVARCHAR(100) NOT NULL;
END;
