-- "Default Report Settings" (the table-wide baseline used by other reports' "Default columns" /
-- "Default dynamic filters" mode) used to be indistinguishable from whichever report happened to
-- carry IsDefault=1 for the table — and AppSeeder.CreateTableWithDefaultsAsync seeds "List All" as
-- that very row for every table. Editing "List All" itself and editing "Default Report Settings"
-- were therefore two UI screens pointed at the exact same Definition JSON: saving either one fully
-- overwrote the other's view of columns/sort/dynamic-filters. IsDefaultSettingsRecord marks a
-- dedicated, hidden Report row per table that Default Report Settings always operates on instead —
-- see GetOrCreateDefaultReportSettingsQueryHandler and RunReportQueryHandler's "Default" branches.
-- IsDefault keeps its original, separate meaning: which report opens when a viewer opens the table.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.Report') AND name = 'IsDefaultSettingsRecord')
BEGIN
    ALTER TABLE meta.Report ADD IsDefaultSettingsRecord BIT NOT NULL CONSTRAINT DF_Report_IsDefaultSettingsRecord DEFAULT 0;
END
GO

-- Backfill: every existing table gets its own hidden settings row, seeded with a copy of whatever
-- its current IsDefault report's Definition already holds — so a table's existing "Default
-- columns"/"Default dynamic filters" behavior doesn't change the moment this migration runs. A
-- table with no IsDefault report at all (data oddity) gets an empty ('{}') definition, matching
-- what an unconfigured Default Report Settings page has always inferred on its own.
INSERT INTO meta.Report
    (AppTableId, OwnerId, Name, Description, ReportType, Visibility,
     Definition, IsDefault, IsDefaultSettingsRecord, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
SELECT
    t.Id,
    ISNULL(src.OwnerId, 0),
    'Default Report Settings',
    NULL,
    'Table',
    'Shared',
    ISNULL(src.Definition, '{}'),
    0,
    1,
    0,
    0,
    SYSUTCDATETIME(),
    ISNULL(src.OwnerId, 0)
FROM meta.AppTable t
OUTER APPLY (
    SELECT TOP 1 r.OwnerId, r.Definition
    FROM meta.Report r
    WHERE r.AppTableId = t.Id AND r.IsDefault = 1 AND r.IsDeleted = 0
    ORDER BY r.Id
) src
WHERE t.IsDeleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM meta.Report ex
      WHERE ex.AppTableId = t.Id AND ex.IsDefaultSettingsRecord = 1 AND ex.IsDeleted = 0
  );
GO
