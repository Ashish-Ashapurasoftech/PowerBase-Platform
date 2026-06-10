-- ── Find tables with duplicate FIDs ─────────────────────────────────────────
SELECT AppTableId, Fid, COUNT(*) AS Cnt,
       STRING_AGG(CAST(Id AS NVARCHAR), ', ') AS FieldIds,
       STRING_AGG(Name, ', ') AS FieldNames,
       STRING_AGG(CAST(IsSystem AS NVARCHAR), ', ') AS AreSystem
FROM meta.AppField
WHERE IsDeleted = 0 AND Fid IS NOT NULL
GROUP BY AppTableId, Fid
HAVING COUNT(*) > 1;

-- ── Fix: null-out FID on duplicate non-system fields (keep system field's FID) ──
-- Review the SELECT above first, then uncomment and run the UPDATE:
/*
UPDATE af
SET af.Fid = NULL
FROM meta.AppField af
WHERE af.IsDeleted = 0
  AND af.IsSystem = 0
  AND af.Fid IS NOT NULL
  AND EXISTS (
      SELECT 1 FROM meta.AppField af2
      WHERE af2.AppTableId = af.AppTableId
        AND af2.Fid = af.Fid
        AND af2.IsSystem = 1
        AND af2.IsDeleted = 0);
*/
