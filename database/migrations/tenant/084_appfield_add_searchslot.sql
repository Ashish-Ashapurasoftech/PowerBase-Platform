-- Azure AI Search slot per field.
-- A field's value is stored in one typed index column (num_01, date_02, str_07, bool_01...). That choice used to be
-- recomputed from the field's position among the table's search-eligible fields, so switching one field on/off, or
-- adding/removing one, moved every later field of the same type to a different column while existing documents kept
-- their values in the old ones. The slot is now stored here and assigned once.
--
-- This script is additive and safe to re-run. It MUST be applied before the application version that selects
-- meta.AppField.SearchSlot is deployed.

IF COL_LENGTH('meta.AppField', 'SearchSlot') IS NULL
    ALTER TABLE meta.AppField ADD SearchSlot NVARCHAR(16) NULL;
GO

-- Backfill: give every currently eligible field the slot the previous position-based rule gave it, so documents already in
-- the index stay correct and nothing has to be reindexed because of this change. Same family rules, same ordering
-- (field number, falling back to Id), same pool sizes (60 num / 40 date / 60 str / 20 bool). Tables with more than 150
-- eligible fields use their own index with f_{fid} columns and get no slots.
;WITH Eligible AS (
    SELECT af.Id,
           af.AppTableId,
           CASE
               WHEN ft.Code IN ('Number', 'Currency', 'Percent', 'Rating', 'NumericRange', 'Formula_Number') THEN 'num'
               WHEN ft.Code IN ('Date', 'DateTime', 'Time', 'Duration', 'DateRange', 'Formula_Date', 'Formula_Time') THEN 'date'
               WHEN ft.Code = 'Boolean' THEN 'bool'
               ELSE 'str'
           END AS Fam,
           COALESCE(af.Fid, af.Id) AS OrderKey,
           COUNT(*) OVER (PARTITION BY af.AppTableId) AS TableEligible
    FROM meta.AppField af
    JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
    WHERE af.IsDeleted = 0
      AND (af.IsFilterable = 1 OR af.IsSearchable = 1)
),
Ranked AS (
    SELECT e.*, ROW_NUMBER() OVER (PARTITION BY e.AppTableId, e.Fam ORDER BY e.OrderKey) AS Pos
    FROM Eligible e
)
UPDATE af
SET af.SearchSlot = r.Fam + '_' + RIGHT('0' + CAST(r.Pos AS VARCHAR(3)), 2)
FROM meta.AppField af
JOIN Ranked r ON r.Id = af.Id
WHERE af.SearchSlot IS NULL
  AND r.TableEligible <= 150
  AND r.Pos <= CASE r.Fam WHEN 'num' THEN 60 WHEN 'date' THEN 40 WHEN 'bool' THEN 20 ELSE 60 END;
GO

-- One slot per table can belong to only one field (deleted fields keep theirs, so a slot is never reused while old
-- documents may still hold the previous value).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AppField_Table_SearchSlot' AND object_id = OBJECT_ID('meta.AppField'))
    CREATE UNIQUE INDEX UX_AppField_Table_SearchSlot
        ON meta.AppField (AppTableId, SearchSlot)
        WHERE SearchSlot IS NOT NULL;
GO
