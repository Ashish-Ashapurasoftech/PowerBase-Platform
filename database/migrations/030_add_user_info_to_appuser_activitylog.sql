-- Phase 1 cleanup: denormalize UserName/UserEmail into meta.AppUser and audit.ActivityLog
-- to eliminate cross-DB JOINs to core.[User] when tenant gets its own DB.

-- meta.AppUser: store user identity at invite time to avoid cross-DB JOIN to core.[User]
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppUser') AND name = 'UserPublicId')
    ALTER TABLE meta.AppUser ADD UserPublicId UNIQUEIDENTIFIER NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppUser') AND name = 'UserName')
    ALTER TABLE meta.AppUser ADD UserName NVARCHAR(256) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('meta.AppUser') AND name = 'UserEmail')
    ALTER TABLE meta.AppUser ADD UserEmail NVARCHAR(256) NULL;
GO

-- Backfill existing rows
UPDATE au
SET au.UserPublicId = u.PublicId,
    au.UserName     = u.Name,
    au.UserEmail    = u.Email
FROM meta.AppUser au
JOIN core.[User] u ON u.Id = au.UserId
WHERE au.UserName IS NULL;
GO

-- audit.ActivityLog: store the actor's name and email at log time
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.ActivityLog') AND name = 'UserName')
    ALTER TABLE audit.ActivityLog ADD UserName NVARCHAR(256) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('audit.ActivityLog') AND name = 'UserEmail')
    ALTER TABLE audit.ActivityLog ADD UserEmail NVARCHAR(256) NULL;
GO

-- Backfill existing rows (best-effort; old rows may have no user)
UPDATE al
SET al.UserName  = u.Name,
    al.UserEmail = u.Email
FROM audit.ActivityLog al
JOIN core.[User] u ON u.Id = al.UserId
WHERE al.UserName IS NULL;
GO
