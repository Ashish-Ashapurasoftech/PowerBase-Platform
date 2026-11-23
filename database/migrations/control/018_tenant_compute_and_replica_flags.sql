-- Per-tenant switches and routing for the stored-columns (compute) feature and the Azure SQL read replica.
-- ComputeEnabled        0 (default) = no ComputeOutbox rows are written and no worker touches the tenant: today's behaviour.
-- ReadReplicaEnabled    0 (default) = every read uses the primary, whatever the mode below says.
-- ReadReplicaMode       0 = none, 1 = built-in replica (ApplicationIntent=ReadOnly on the same database), 2 = named replica database
-- ReadReplicaDatabaseName  the named replica's database (mode 2 only)
-- All defaults are constants (metadata-only change). Additive and safe to re-run.

IF COL_LENGTH('meta.Tenant', 'ComputeEnabled') IS NULL
    ALTER TABLE meta.Tenant ADD ComputeEnabled BIT NOT NULL CONSTRAINT DF_Tenant_ComputeEnabled DEFAULT 0;
GO

IF COL_LENGTH('meta.Tenant', 'ReadReplicaEnabled') IS NULL
    ALTER TABLE meta.Tenant ADD ReadReplicaEnabled BIT NOT NULL CONSTRAINT DF_Tenant_ReadReplicaEnabled DEFAULT 0;
GO

IF COL_LENGTH('meta.Tenant', 'ReadReplicaMode') IS NULL
    ALTER TABLE meta.Tenant ADD ReadReplicaMode TINYINT NOT NULL CONSTRAINT DF_Tenant_ReadReplicaMode DEFAULT 0;
GO

IF COL_LENGTH('meta.Tenant', 'ReadReplicaDatabaseName') IS NULL
    ALTER TABLE meta.Tenant ADD ReadReplicaDatabaseName NVARCHAR(128) NULL;
GO
