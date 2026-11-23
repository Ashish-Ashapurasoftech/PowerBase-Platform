-- Single-row heartbeat the compute worker writes on the PRIMARY every few seconds. Readers read it from the read replica: if it is
-- older than the allowed lag, the replica is behind and the read goes to the primary instead (see TenantConnectionFactory.CreateReadOnlyAsync).
-- Nothing writes it until the read-replica feature is switched on for the tenant. Additive and safe to re-run.

IF OBJECT_ID('meta.ReplicaHeartbeat') IS NULL
BEGIN
    CREATE TABLE meta.ReplicaHeartbeat (
        Id      TINYINT      NOT NULL CONSTRAINT PK_ReplicaHeartbeat PRIMARY KEY CONSTRAINT CK_ReplicaHeartbeat_Single CHECK (Id = 1),
        BeatOn  DATETIME2(3) NOT NULL CONSTRAINT DF_ReplicaHeartbeat_BeatOn DEFAULT SYSUTCDATETIME()
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM meta.ReplicaHeartbeat WHERE Id = 1)
    INSERT INTO meta.ReplicaHeartbeat (Id) VALUES (1);
GO
