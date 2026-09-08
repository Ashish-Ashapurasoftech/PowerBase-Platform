-- Migration: 054_appuser_unique_indexes_per_assignment.sql
-- Description: Drop UX_AppUser_AppId_UserId_AppRoleId and add filtered unique indexes for group and individual assignments

IF EXISTS (
    SELECT 1 
    FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND i.name = 'UX_AppUser_AppId_UserId_AppRoleId'
)
BEGIN
    DROP INDEX UX_AppUser_AppId_UserId_AppRoleId ON meta.AppUser;
END
GO

IF NOT EXISTS (
    SELECT 1 
    FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND i.name = 'UX_AppUser_GroupAssignment'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppUser_GroupAssignment 
    ON meta.AppUser(AppId, UserId, AppRoleId, GroupId) 
    WHERE IsDeleted = 0 AND IsFromGroup = 1 AND GroupId IS NOT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 
    FROM sys.indexes i
    JOIN sys.tables t ON t.object_id = i.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = 'meta' AND t.name = 'AppUser' AND i.name = 'UX_AppUser_IndividualAssignment'
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_AppUser_IndividualAssignment 
    ON meta.AppUser(AppId, UserId, AppRoleId) 
    WHERE IsDeleted = 0 AND IsFromGroup = 0;
END
GO
