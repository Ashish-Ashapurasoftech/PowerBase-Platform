-- Migration: 050_allow_multi_role_appuser.sql
-- Description: Drop unique constraint UX_AppUser_AppId_UserId and add UX_AppUser_AppId_UserId_AppRoleId to allow different roles per user while preventing duplicate same-role assignments.

IF EXISTS (
    SELECT 1 
    FROM sys.key_constraints kc
    JOIN sys.tables t ON t.object_id = kc.parent_object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND kc.name = 'UX_AppUser_AppId_UserId'
)
BEGIN
    ALTER TABLE meta.AppUser DROP CONSTRAINT UX_AppUser_AppId_UserId;
END
GO

IF EXISTS (
    SELECT 1 
    FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND i.name = 'UX_AppUser_AppId_UserId'
)
BEGIN
    DROP INDEX UX_AppUser_AppId_UserId ON meta.AppUser;
END
GO

IF NOT EXISTS (
    SELECT 1 
    FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND i.name = 'UX_AppUser_AppId_UserId_AppRoleId'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppUser_AppId_UserId_AppRoleId 
    ON meta.AppUser(AppId, UserId, AppRoleId) 
    WHERE IsDeleted = 0;
END
GO
