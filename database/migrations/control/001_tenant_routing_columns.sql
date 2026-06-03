-- Add per-tenant routing columns to meta.Tenant for database-per-tenant support.
-- Existing tenants are seeded with ProvisioningState=Ready pointing at the current
-- shared database so the resolver works without any code changes in Phase 0.

ALTER TABLE meta.Tenant
    ADD DatabaseName        NVARCHAR(128) NULL,
        ServerRef           NVARCHAR(256) NULL,
        ConnectionSecretRef NVARCHAR(256) NULL,
        ProvisioningState   NVARCHAR(32)  NOT NULL CONSTRAINT DF_Tenant_ProvisioningState DEFAULT 'Ready',
        SchemaVersion       INT           NULL;
GO

-- Backfill existing tenants to point at the current shared database.
DECLARE @dbName NVARCHAR(128) = DB_NAME();

UPDATE meta.Tenant
SET DatabaseName      = @dbName,
    ProvisioningState = 'Ready',
    SchemaVersion     = 0
WHERE DatabaseName IS NULL;
GO
