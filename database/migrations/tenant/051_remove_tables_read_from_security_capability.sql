-- Migration: 051_remove_tables_read_from_security_capability.sql
-- Description: Remove tables:read and tables:create permissions from the Security & Role Manager capability.

DELETE cp
FROM meta.CapabilityPermission cp
JOIN meta.Capability c ON c.Id = cp.CapabilityId
JOIN meta.Permission p ON p.Id = cp.PermissionId
WHERE c.Code = 'security' AND p.Code IN ('tables:read', 'tables:create');
GO
