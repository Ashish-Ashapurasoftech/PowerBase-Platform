-- Backfill Participant AppRole for all existing apps that don't already have one.
-- New apps get all 3 built-in roles (Administrator, Participant, Viewer) via CreateAppCommandHandler.
IF NOT EXISTS (SELECT 1 FROM meta.AppRole WHERE Name = 'Participant' AND IsSystem = 1 AND IsDeleted = 0)
BEGIN
    INSERT INTO meta.AppRole (AppId, TenantId, Name, IsDefault, IsSystem, CreatedBy)
    SELECT a.Id, a.TenantId, 'Participant', 0, 1, 0
    FROM meta.App a
    WHERE a.IsDeleted = 0
      AND NOT EXISTS (
          SELECT 1 FROM meta.AppRole ar
          WHERE ar.AppId = a.Id AND ar.Name = 'Participant' AND ar.IsDeleted = 0
      );
END
GO
