-- A duplicate against the destination should point at the record that already holds the value.
IF COL_LENGTH('meta.ImportRunIssue', 'ExistingRecordRef') IS NULL
    ALTER TABLE meta.ImportRunIssue ADD ExistingRecordRef BIGINT NULL;
GO
