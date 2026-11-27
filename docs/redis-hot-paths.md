# Redis on the server and the cached hot paths (branch feature/PB-redis-hot-paths)

## 1. Server settings checklist (Azure App Service → Configuration → Application settings)

App Service uses `__` for `:`. Set these on **every** API instance and on the ComputeWorker container app.

| Setting | Value | Why |
|---|---|---|
| `MetadataCache__Enabled` | `true` | Kill switch (false = every read from SQL). |
| `MetadataCache__SharedVersions` | `true` | Needed for more than one instance. Without it, security groups (Access, UserPermissions, Sessions, Users, Tenants, UserTokens, ConnectedTables) have no versions to confirm and are read from SQL every time. |
| `MetadataCache__Redis__Host` | `<name>.<region>.redis.azure.net:10000` | **Production way**: Entra ID with the app's managed identity, TLS on. The identity needs the Redis data access policy (Data Owner/Contributor). |
| `MetadataCache__Redis__ManagedIdentityClientId` | client id, or leave empty | Only for a user-assigned identity. |
| — or — `MetadataCache__Redis__ConnectionString` | `host:10000,password=…,ssl=True,abortConnect=False` (from Key Vault) | Key-based alternative. Do not set both. |
| `MetadataCache__Redis__InstanceName` | leave empty | Default `powerbase:{environment}:`, so UAT and prod can share one Redis. |
| `MetadataCache__UseInMemoryCache` | see §2 | |
| `MetadataCache__L2TimeoutMs` | `100` (default) | One slow read past this turns L2 off for 30 s on that instance (the log says "Metadata L2 cache (Redis) unavailable"). Raise to 200–300 if the logs show it. |
| `ResultCache__Enabled` | `true` | Report result cache (Summary/Chart, unencrypted apps, ≤ 60 s). Only works with Redis configured. |
| `ReadReplica__Enabled` | unchanged | Independent of Redis. Cache versions are always read from the primary. |
| `ServerTiming__Enabled` | `true` (default) | Needed for the measurements in §4. |

`MetadataCache:Provider` / `RedisConnectionString` / `AzureCacheConnectionString` still work, but `Redis:Host` or `Redis:ConnectionString` is the supported path.

**Migrations.** `meta.CacheVersion` must exist in the control DB (`database/migrations/control/019_create_cache_version.sql`) and in every tenant DB (`tenant/104_create_cache_version.sql`). If it is missing, every security group fails closed (SQL every request, a warning per request "Cache versions of tenant … could not be read"). Check:

```sql
SELECT OBJECT_ID('meta.CacheVersion');  -- run in the control DB and in each tenant DB: must not be NULL
```

No new migration in this branch. The new groups are created as rows on their first bump.

## 2. UseInMemoryCache: decision

The default stays `false` (decision 2026-11-27), but **switching it on is safe and is the bigger latency win**:

- With it off, every cached key is one Redis round trip (~1–3 ms each, in sequence). `/apps/{id}/permissions` reads ~10 keys, a record list ~15.
- With it on, each request still reads the shared versions from SQL once per tenant (CacheVersionScopeMiddleware, also now for parallel uses, see §3), and a moved version drops the group at once. So an in-memory copy is never used after a committed write on another instance. Hits then cost ~0 ms.
- `VersionCheckIntervalMs` only applies outside a request (background loops). Leave it at 1000.

Keep the versions in SQL (`SqlCacheVersionStore`), not Redis. They are the fail-closed proof for permissions and must survive a Redis outage. They cost 1 query per tenant per request (plus 1 on the control DB for the global groups), visible as `cache-sync` in Server-Timing.

## 3. What changed

### Diagnostics
- `Server-Timing` now carries (only what happened):
  - `cache-sync;dur;desc="n"` — the shared-version reads.
  - `cache-l1;desc="hits"`.
  - `cache-l2;dur;desc="Xhit/Ymiss"` — Redis.
  - `cache-sql;dur;desc="n"` — cache misses answered by SQL, `(k bypass)` when not cacheable.
  - `db-conn;desc="Ntenant/Mcontrol"` — every SQL connection the request opened, cached or not. This is the number to compare before/after.
- Files: `CacheRequestStats` (Application/Common/Caching), `MetadataCache`, `ServerTiming`, `ServerTimingMiddleware`, `TenantConnectionFactory`, `ControlConnectionFactory`.

### Correctness fixes found on the way
- `CapabilityRepository` rewrote `meta.AppRolePermission` without dropping the cached `appperms:` entries. A removed capability kept working until the 24 h TTL. It now drops Access first.
- Parallel cache uses in one request (Task.WhenAll) could answer before the request's shared-version read finished. `CacheVersionScope.SyncOnceAsync` now makes them all wait for that one read.
- `AppRepository.SetQuickbaseIdsAsync` changed cached apps/tables without invalidating them. `AppRoleRepository.UpdateNameAsync` and `SetHomePageAsync` did not drop Access. `AppUserRepository.CreateAsync` in a transaction used the short quarantine.
- Copy App drops Apps before `usp_FinishAppCopy` (Draft → Active) and Apps/Ids on discard.

### New cache entries (group → key → dropped by)
| Group | Key | Read by | Dropped by |
|---|---|---|---|
| Access | `rolename:{app}:{user}`, `rolenames:{app}:{user}` | /permissions, /my-roles | AppUser/AppRole/Group writes (role rename now too) |
| Access | `tableperms:{role}`, `fieldperms:{role}`, `recordfilters:{role}` | /permissions | AppRolePermission writes, field writes, **table writes** (TablesChanging now drops Access) |
| Access | `navpages:{app}:u{user}` | page nav | every page write, role assignments, role home page |
| UserPermissions (global) | `tenantrole:{user}:{tenant}` | JwtMiddleware (API tokens) | TenantUser/TenantRole writes (unchanged) |
| Users (global, security) | `signin:{user}` (no password hash) | JwtMiddleware, current user public id | UserRepository writes, Admin activate/system role |
| Tenants (global, security) | `id:{tenant}` | /permissions (Code Pages) | Admin status / Code Pages, provisioning |
| UserTokens (global, security) | `hash:{sha}`, `apps:{tokenId}` | JwtMiddleware (pb_ut_) | create, status, revoke, rotate, details (not last-used) |
| Ids | `tableid:`, `form:`, `formrule:`, `page:` | GetByPublicId / membership checks | deletes (+ form/rule/page writes) |
| Apps | via Ids + `id:` | AppRepository.GetByPublicIdAsync | AppsChanging |
| Tables | `nav:{app}`, `aliases:{app}`, by public id | nav, formulas | TablesChanging. Table detail reads the live RecordCount (`GetRecordCountAsync`). |
| Fields | `app:{app}`, `pub:{field}` | ListByApp / GetByPublicId | FieldsChanging + conversion/encryption services |
| FieldTypes | `all` per tenant | field type lookups | migrations only (TTL backstop) |
| Variables | `app:{app}` | formulas, pipelines | create/update/delete |
| Forms | `form:`, `table:`, `pages:`, `layout:`, `rules:form:`, `rules:table:`, `gridedit:runtime:`, `gridedit:applied:` | form render, rule runs, grid edit | every Form/FormRule write, grid-edit config, field and table writes |
| ConnectedTables (security) | `tables` | connected-table write filter | create / soft-delete (replaces the 30 s per-instance static cache) |

`GetAppPermissionsQueryHandler` now runs its independent reads and the per-role lists in parallel.

### Process-local caches
- `ConnectedTableCache`: moved onto the shared cache (see above). No more 30 s window on other instances.
- `AppCopyProgressStore`: mirrors each step to Redis (in order) when Redis is configured, so a poll on another instance sees the progress. Data copies already used SQL.
- `ComputeSettings`: **not changed**. ComputeEnabled is set by ops directly in SQL, so a version-invalidated cache would stay stale up to the 24 h TTL. The current short `SettingsCacheSeconds` TTL is the right tool.
- `MaterializedFieldStateRepository`: **not changed**. Its state is written constantly by the ComputeWorker. A 5 s per-instance TTL is cheaper than a version bump per state change.

### Frontend
- `RequestCache` (core/utils): in-flight dedup + 15 s reuse. It is used by `TablesService.listNav` (16 call sites on navigation) and cleared by the service's own table writes.
- Not done, and why:
  - `pagesService.listNav` has one caller.
  - `AppPermissionService` already dedups `/permissions` + `/my-roles` per app.
  - `reportsService.listByTable` / `formsService.getLayout` / `relationshipsService.listByTable` are mutated from many components outside their service, so a client cache there needs per-mutation hooks. They are now cached on the server.

## 4. Before/after measurement (to do on UAT C)

I have not measured anything yet; the numbers come from the Network tab once this is deployed. For `/apps/{id}/permissions`, `/apps/{id}/my-roles`, `/apps/{id}/tables/nav`, `/apps/{id}/pages/nav`:
1. Open DevTools → Network → the request → Timing. The Server-Timing section shows `total`, `auth`, `cache-sync`, `cache-l2`, `cache-sql` and `db-conn`.
2. Note the first request after a deploy (cold), then the second (warm).
3. Expected from the code (for one role, to be confirmed):
   - `/permissions` went from ~9 sequential SQL round trips to 1 `cache-sync` + Redis hits.
   - `db-conn` should show `0tenant/0–1control` when warm.
4. If `cache-l2` shows misses on every warm request, L2 is off. Check the log for "Metadata L2 cache (Redis) unavailable" and the "Cache metrics since start" line (every 5 min).
