-- Migration 073: meta.AppField.IsHidden — a real flag for helper fields (e.g. the "(original)" Text field a
-- relationship conversion creates) that must stay out of every user-facing field list.
-- Replaces the "hiddenFromFieldList":true marker that used to live inside AppField.Settings.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'IsHidden')
BEGIN
    ALTER TABLE meta.AppField ADD IsHidden BIT NOT NULL CONSTRAINT DF_AppField_IsHidden DEFAULT 0;
END
GO

-- Backfill from the old Settings marker (the helper field's Settings held nothing else, so clear it).
-- Dynamic SQL so this batch compiles even on the run that adds the column.
EXEC('
UPDATE meta.AppField
SET IsHidden = 1,
    Settings = NULL
WHERE IsHidden = 0
  AND Settings LIKE ''%"hiddenFromFieldList":true%'';
');
GO
