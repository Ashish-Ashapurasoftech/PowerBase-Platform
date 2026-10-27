-- Shared values: when set, the field's record values may be used as the source of another
-- Single/Multi Select field's choices ("From another field"), including from other apps.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'IsSharedValues')
BEGIN
    ALTER TABLE meta.AppField ADD IsSharedValues BIT NOT NULL CONSTRAINT DF_AppField_IsSharedValues DEFAULT 0;
END
GO
