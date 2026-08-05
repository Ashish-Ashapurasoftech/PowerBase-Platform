-- Migration: Add ShowInUserPickers column to meta.AppUser
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppUser') AND name = 'ShowInUserPickers')
BEGIN
    ALTER TABLE meta.AppUser ADD ShowInUserPickers BIT NOT NULL CONSTRAINT DF_AppUser_ShowInUserPickers DEFAULT 1;
END
