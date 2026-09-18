-- Auto-fill now defaults to ON for every field type that supports it (see
-- FieldAutoFillCapability.cs for the authoritative allow-list) — this backfills existing rows to
-- match, since 056 only set the DEFAULT for future inserts and couldn't retroactively touch rows
-- that already existed before that column was added (some of which may have been inserted while
-- 056 still carried its original DEFAULT 0, before this file changed it to DEFAULT 1).
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppField') AND name = 'IsAutoFill')
BEGIN
    UPDATE af
    SET af.IsAutoFill = 1
    FROM meta.AppField af
    JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
    WHERE af.IsSystem = 0
      AND af.IsDeleted = 0
      AND af.IsAutoFill = 0
      AND ft.Code IN (
          'Text', 'RichText', 'TextMultiLine', 'MultiSelect', 'SingleSelect',
          'Number', 'Currency', 'Percent', 'Rating',
          'Date', 'DateTime', 'Time', 'Duration',
          'Boolean',
          'User', 'MultiUser',
          'Phone', 'Email', 'Url', 'Address'
      );
END
GO
