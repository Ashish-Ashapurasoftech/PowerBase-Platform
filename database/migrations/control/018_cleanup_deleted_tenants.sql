-- Migration 018: Mark deleted/decommissioned tenant databases as Deleted in meta.Tenant
-- Tenants 8, 9, 14, 15 databases were dropped on Azure SQL; mark them deleted so workers and migrators skip them.
IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'meta' AND t.name = 'Tenant')
BEGIN
    UPDATE meta.Tenant
    SET IsDeleted = 1, Status = 'Deleted', ProvisioningState = 'Deleted'
    WHERE Id IN (8, 9, 14, 15) AND (ProvisioningState <> 'Deleted' OR IsDeleted = 0);
END
GO
