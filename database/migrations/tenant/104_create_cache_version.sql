-- Shared versions of the API's metadata cache groups (fields, tables, apps, access, ids …), one row per group. Every write that
-- invalidates a group bumps its row, so EVERY API instance sees the change on its next request (the cache itself is in each
-- process's memory; this row is the authoritative signal). Read on the primary only. Additive and safe to re-run.

IF OBJECT_ID('meta.CacheVersion') IS NULL
BEGIN
    CREATE TABLE meta.CacheVersion (
        Grp        NVARCHAR(32) NOT NULL CONSTRAINT PK_CacheVersion PRIMARY KEY,
        V          BIGINT       NOT NULL CONSTRAINT DF_CacheVersion_V DEFAULT 0,
        ChangedOn  DATETIME2(3) NOT NULL CONSTRAINT DF_CacheVersion_ChangedOn DEFAULT SYSUTCDATETIME()
    );
END
GO
