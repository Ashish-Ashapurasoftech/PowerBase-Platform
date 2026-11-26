-- Decision Q4 (2026-11-26): every Lookup / Summary / Formula field goes to the Azure AI Search index, so it is always Searchable AND
-- Filterable (locked from now on: create, update, restore, import and type change keep both on; see ComputedFieldSearch).
-- This switches on the fields that existed before. Hidden helper fields and system fields are left alone, as the code does.
-- Nothing is sent to the index here: a field's values go there when its stored column (c_{fid}) is Ready and its field job runs.
-- Search slots are allocated by the indexer the next time the table is indexed. Safe to re-run (only rows still off change).

UPDATE af
SET IsSearchable = 1,
    IsFilterable = 1
FROM meta.AppField af
JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
WHERE af.IsDeleted = 0
  AND af.IsHidden = 0
  AND af.IsSystem = 0
  AND (ft.Code IN ('Lookup', 'Summary', 'Formula') OR ft.Code LIKE 'Formula[_]%')
  AND (af.IsSearchable = 0 OR af.IsFilterable = 0);
GO
