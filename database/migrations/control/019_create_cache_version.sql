-- Shared versions of the control-database cache groups (user permission codes, session revocation), one row per group: the
-- same mechanism as tenant migration 104 (meta.CacheVersion), for entries that are not bound to a tenant. Additive and safe to re-run.

IF OBJECT_ID('meta.CacheVersion') IS NULL
BEGIN
    CREATE TABLE meta.CacheVersion (
        Grp        NVARCHAR(32) NOT NULL CONSTRAINT PK_CacheVersion PRIMARY KEY,
        V          BIGINT       NOT NULL CONSTRAINT DF_CacheVersion_V DEFAULT 0,
        ChangedOn  DATETIME2(3) NOT NULL CONSTRAINT DF_CacheVersion_ChangedOn DEFAULT SYSUTCDATETIME()
    );
END
GO
