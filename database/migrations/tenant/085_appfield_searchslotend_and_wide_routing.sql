-- Azure AI Search: second slot for range fields, and persisted wide-table routing.
-- Additive and safe to re-run. Apply after 084. The application also runs (answering from SQL) before this is applied.

-- A NumericRange / DateRange field is two values. Its first value lives in SearchSlot, its second in SearchSlotEnd.
IF COL_LENGTH('meta.AppField', 'SearchSlotEnd') IS NULL
    ALTER TABLE meta.AppField ADD SearchSlotEnd NVARCHAR(16) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AppField_Table_SearchSlotEnd' AND object_id = OBJECT_ID('meta.AppField'))
    CREATE UNIQUE INDEX UX_AppField_Table_SearchSlotEnd
        ON meta.AppField (AppTableId, SearchSlotEnd)
        WHERE SearchSlotEnd IS NOT NULL;
GO

-- Wide tables. A table with more than 150 search-eligible fields keeps its documents in its own index (pb-tbl-{tableId})
-- with one column per field (f_{fid}, and f_{fid}_e for a range's second value). Whether a table is wide used to be
-- recomputed from its field count, so the moment a field was switched on, queries moved to the new index while the
-- documents were still in the old one. It is now stored: a table is wide when its fields carry "f_" slots, and that is
-- written only after the documents have been copied. This marks the tables that are already wide by the old rule, so
-- they keep using the index they already have.
;WITH WideTables AS (
    SELECT af.AppTableId
    FROM meta.AppField af
    WHERE af.IsDeleted = 0 AND (af.IsFilterable = 1 OR af.IsSearchable = 1)
    GROUP BY af.AppTableId
    HAVING COUNT(*) > 150
)
UPDATE af
SET af.SearchSlot = 'f_' + CAST(af.Fid AS VARCHAR(11)),
    af.SearchSlotEnd = CASE WHEN ft.Code IN ('NumericRange', 'DateRange') THEN 'f_' + CAST(af.Fid AS VARCHAR(11)) + '_e' END
FROM meta.AppField af
JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
JOIN WideTables w ON w.AppTableId = af.AppTableId
WHERE af.IsDeleted = 0
  AND af.Fid IS NOT NULL
  AND (af.IsFilterable = 1 OR af.IsSearchable = 1)
  AND (af.SearchSlot IS NULL OR af.SearchSlot NOT LIKE 'f[_]%');
GO
