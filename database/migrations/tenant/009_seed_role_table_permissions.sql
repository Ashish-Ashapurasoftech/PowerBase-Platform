-- Backfill: ensure every (AppRole × AppTable) pair that belongs to the same App
-- has an AppRoleTablePermission row. Canonical default: View=AllRecords, Modify=None,
-- all boolean flags off, FieldAccessLevel=FullAccess.
-- Safe to run multiple times (NOT EXISTS guard).

INSERT INTO meta.AppRoleTablePermission
    (AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
     CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel)
SELECT r.Id, t.Id,
       'AllRecords', 'None', 0, 0, 0, 0, 'FullAccess'
FROM meta.AppRole r
JOIN meta.AppTable t ON t.AppId = r.AppId AND t.IsDeleted = 0
WHERE r.IsDeleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM meta.AppRoleTablePermission x
      WHERE x.AppRoleId = r.Id AND x.AppTableId = t.Id
  );
GO
