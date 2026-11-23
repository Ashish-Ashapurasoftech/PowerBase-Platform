-- The physical name of a field's stored column (e.g. 'c_14'), next to PhysicalColumnName (the f_{fid} column).
-- Set when the worker starts building the stored column; NULL while the field has no stored column.
-- Nothing reads it to decide how to query: reads still derive c_{fid} from the Fid. It is there so the column can be found from meta.AppField.
-- Nullable, no default: a metadata-only change. Additive and safe to re-run.

IF COL_LENGTH('meta.AppField', 'MaterializedColumnName') IS NULL
    ALTER TABLE meta.AppField ADD MaterializedColumnName NVARCHAR(128) NULL;
GO

-- Fill in the fields whose stored column already exists in data.t_{AppTableId}.
UPDATE af
SET MaterializedColumnName = CONCAT('c_', af.Fid)
FROM meta.AppField af
WHERE af.MaterializedColumnName IS NULL
  AND af.Fid IS NOT NULL
  AND EXISTS (SELECT 1 FROM sys.columns c
              WHERE c.object_id = OBJECT_ID(CONCAT('data.t_', af.AppTableId))
                AND c.name = CONCAT('c_', af.Fid));
GO
