-- Migration 033: Allow platform-level user invites without a pre-assigned tenant.
-- Makes audit.InviteToken.TenantId and TenantRoleId nullable so SuperAdmin can invite
-- a user to the platform without assigning them to a specific tenant immediately.

-- Drop existing FK constraints
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InviteToken_Tenant')
    ALTER TABLE audit.InviteToken DROP CONSTRAINT FK_InviteToken_Tenant;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_InviteToken_TenantRole')
    ALTER TABLE audit.InviteToken DROP CONSTRAINT FK_InviteToken_TenantRole;

-- Make columns nullable
ALTER TABLE audit.InviteToken ALTER COLUMN TenantId BIGINT NULL;
ALTER TABLE audit.InviteToken ALTER COLUMN TenantRoleId BIGINT NULL;

-- Re-add FKs (nullable FK allows NULL in SQL Server)
ALTER TABLE audit.InviteToken
    ADD CONSTRAINT FK_InviteToken_Tenant     FOREIGN KEY (TenantId)     REFERENCES meta.Tenant(Id);

ALTER TABLE audit.InviteToken
    ADD CONSTRAINT FK_InviteToken_TenantRole FOREIGN KEY (TenantRoleId) REFERENCES meta.TenantRole(Id);
GO
