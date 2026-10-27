# Search pipeline: how it works and how to run it

## How a record reaches search

1. A record is saved. **In the same database transaction** a row is written to `meta.SearchOutbox` (table, record id, version; no values).
2. Right after the commit the change is published to Service Bus (`search-indexing-queue`, or the priority queue) with the changed values attached.
3. `SearchIndexerWorker` reads a batch, decides where each record's values come from, and uploads to Azure AI Search:
   - first delivery and nothing newer in the index: the message's values;
   - redelivered, older than what the index holds, or no values attached: the record is **re-read from SQL** (the source of truth).
4. When the index really holds the record, the worker **deletes its outbox row**.
5. `SearchOutboxRelay` re-publishes any row still there after `GraceSeconds` (message lost, indexer down, indexing failed). It carries no values, so the indexer reads SQL. Rows that run out of attempts wait for the daily check.
6. Once a day (`DailySweepHourUtc`, UTC) the relay checks every tenant, **logs what is still unconfirmed** and gives rows that gave up a new round.

A change is therefore lost only if the database transaction itself is lost.

Not covered by an event: direct SQL edits and migrations. Use the admin backfill (below) after those.

## Settings (appsettings.json)

| Key | Default | Meaning |
|---|---|---|
| `AzureAiSearch:IndexName` | `powerbase-records-index` | The shared index. |
| `AzureAiSearch:ShardCount` | `1` | `N > 1`: tenants spread over `pb-shard-001..N` by tenant id. A tenant is never split. Changing it needs a full reindex. |
| `AzureAiSearch:IsMultipleIndex` + `DedicatedTenantIds` | `false` / empty | Listed tenants get their own index `pb-tenant-{id}`. |
| `AzureAiSearch:WideTableIndexBudget` | `10` | Tables that may have their own index (`pb-tbl-{id}`, more than 150 search fields). Each counts against the service's index limit. |
| `AzureAiSearch:UseManagedIdentity` | `false` | Sign in as the app identity instead of `ApiKey`. Roles: *Search Service Contributor*, *Search Index Data Contributor*. |
| `AzureAiSearch:EncryptionKey:*` | none | Customer-managed key, applied **only when an index is created**. |
| `AzureAiSearch:AllowDestructiveAdmin` | `false` | Needed to drop an index or purge a tenant. |
| `AzureServiceBus:FullyQualifiedNamespace` | none | Managed identity instead of `ConnectionString`. Roles: *Azure Service Bus Data Sender* / *Data Receiver*. |
| `AzureServiceBus:SearchIndexPriorityQueueEnabled` | `true` | `false` where the priority queue does not exist. |
| `AzureServiceBus:MaxDeliveryAttempts` | `10` | Deliveries after which the worker dead-letters with the error text. Keep at or below the queue's own limit. |
| `Search:Indexer:Enabled` | `true` | Run the indexer in this instance. |
| `Search:Outbox:*` | see file | Relay timing and limits. `Enabled=false` stops the relay. |

## Health endpoint

`GET /health/search` (no authentication; counts and times only).

- **Unhealthy (503)**: the search service is not answering, or an indexer worker has not looked at its queue for 5 minutes.
- **Degraded (200)**: changes have waited longer than `StaleAfterMinutes`, or rows gave up.
- Body: `status`, `description`, and details (`outboxPending`, `outboxParked`, `outboxOldestMinutes`, `indexedDocuments`, `rejectedDocuments`, `deadLettered`, worker last-loop ages).

Use it for an availability test. Alert rules for the Azure side are in `infra/search-alerts.bicep` (not deployed; review before use).

## Alerts worth having

| Alert | Source | Why |
|---|---|---|
| Queue backlog | Service Bus `ActiveMessages` high for 15 min | indexer stopped or too slow |
| Dead letters | Service Bus `DeadletteredMessages` > 0 | permanent failures; read the dead-letter reason |
| Throttling | Service Bus `ThrottledRequests`, Search `ThrottledSearchQueriesPercentage` | sustained load; scale or slow down |
| Pipeline health | availability test on `/health/search` | indexer down, outbox stuck |
| Outbox | log: `Search outbox: ... waiting` / `daily check` warnings | changes not reaching the index |

## Runbook

**Search results are missing recent records.** Check `/health/search`. Look at `outboxPending`: if it is falling, the pipeline is catching up. If it is stuck or `outboxParked` > 0, read the worker's error logs (Application Insights, search for `Search index`), then fix the cause (Azure Search or Service Bus outage, a rejected document). The relay and the daily check re-drive the rows.

**Dead-letter queue is not empty.** Peek (non-destructive) and group by `DeadLetterReason`: `IndexingRejected` (Azure refused the document: the error text says why), `IndexingFailed` / `BulkUploadFailed` (temporary failures that ran out of deliveries), `ProcessingError` (the worker could not process it), `MaxDeliveryCountExceeded` (broker limit). The records stay in the outbox; after fixing the cause they are indexed by the relay, or run a table backfill.

**A table's data looks wrong or incomplete in search.** Run the backfill for the tenant (`POST /admin/search/backfill`, optionally for one table). It reads SQL and uses the same mapping as the indexer.

**After direct SQL changes or a migration that edits records.** Run the backfill for the affected tenant/table.

**Priority queue errors in the log.** The queue does not exist. Create it, or set `SearchIndexPriorityQueueEnabled=false`.

**Rebuild the index (schema change, customer-managed key).** Create the new index next to the old one (it gets the key at creation), backfill into it, switch `IndexName`, keep the old one until verified. `reset-index` / `create-index?force=true` drop the live index and need `AllowDestructiveAdmin=true` plus `confirm=<index name>`.

**Offboard a tenant.** `POST /admin/search/purge-tenant?confirm=<tenant id>` (needs `AllowDestructiveAdmin=true`) removes the tenant's documents from the search indexes. The database is not touched.

## Deploy order

1. Run tenant migrations `084`, `085`, `086` on every tenant database (the application also runs without them, answering search from SQL and skipping the outbox, but nothing is protected until they are applied).
2. Deploy the application.
3. Run the backfill per tenant once so range and duration fields are indexed.
4. Create the priority queue (or switch it off), and optionally configure managed identity and the key.

## Security notes

- The index holds **plaintext copies** of every field that is searchable or filterable (that is what makes search fast on encrypted data). Treat the search service as sensitive as the database: private endpoint with public access disabled, managed identity instead of keys, a customer-managed key, diagnostic logs on, and no human access to the data plane.
- `meta.SearchOutbox` holds ids and versions only.
- The health endpoint exposes counts only.
