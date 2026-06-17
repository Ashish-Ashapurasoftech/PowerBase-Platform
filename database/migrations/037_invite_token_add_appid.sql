-- Adds AppId and AppRoleId columns to audit.InviteToken so that app-level invites
-- can automatically grant app membership when the invited user accepts their setup link.
-- No foreign keys are created because meta.App is in the tenant database, while audit.InviteToken is in the control database.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('audit.InviteToken') AND name = 'AppId'
)
BEGIN
    ALTER TABLE audit.InviteToken
        ADD AppId     BIGINT NULL,
            AppRoleId BIGINT NULL;
END
