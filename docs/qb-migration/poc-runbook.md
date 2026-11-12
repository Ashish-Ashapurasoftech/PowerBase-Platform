# QB → PB migration: POC runbook (Stage 1)

How to run the POC once the team has delivered the QuickBase test app (see `poc-quickbase-app-spec.md`).

## What was built
| Piece | Where | What it does |
|---|---|---|
| State tables | `database/qb-migration/001_qbm_state.sql` | `qbm` schema: Run, TableMap, FieldMap, Chunk (ledger), Issue. Applied **only** to the tenant being migrated, by `init`. Not in `database/migrations`, so no other tenant and no CI run gets it |
| Core logic | `src/PowerBase.Application/QbMigration/` | Field mapping, value transforms, Record ID range planning, page-by-page chunk reading, tier order |
| SQL and HTTP | `src/PowerBase.Infrastructure/QbMigration/` | QuickBase client (rate limit, retry-after, backoff, timeout → halve), chunk loader (keeps QuickBase ids, encrypts and fails closed, replay-safe), state store, PowerBase metadata reader, quiet user provisioning |
| Worker | `src/PowerBase.QbMigration.Worker/` | The command-line worker. Doesn't reference `PowerBase.API` |
| Tests | `tests/PowerBase.QbMigration.Tests/` | 75 tests, 22 of them against real SQL in tempdb |

No existing file was changed, and nothing is registered in the API. The new projects are not in `PowerBase.sln` yet, so CI doesn't build them.

## Using an existing real QuickBase app (chosen for the first local run)
- **Pick a small app:** a few tables, up to tens of thousands of records. Prefer one with relationships, user fields and an address, so the risky cases get covered.
- **Read-only on the QuickBase side:** the worker only reads (tables, fields, records), using a user token with view access to that app. Nothing in QuickBase is changed.
- **Real data lands on your machine:** it goes into the local dev database (`Powerbase_19`). Treat that database as holding production data, and delete the POC app when you're done.
- **Keep QuickBase unchanged during the test:** ask users not to edit the app between `run` and `validate`, or edits show up as gate 3 differences. Live changes are handled later by the delta passes.
- **The app spec is optional now:** `poc-quickbase-app-spec.md` is still useful later to cover the edge cases a real app might not have.

## One-time setup (local)
Store secrets with user-secrets, never in `appsettings.json`:
```bash
cd src/PowerBase.QbMigration.Worker
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=DESKTOP-069FFBI;Database=Powerbase;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet user-secrets set "Encryption:MasterKey" "<same master key the dev API uses>"
dotnet user-secrets set "QuickBase:RealmHostname" "<realm>.quickbase.com"
dotnet user-secrets set "QuickBase:UserToken" "<dedicated POC user token>"
```
Always build in **Release**: `dotnet build src/PowerBase.QbMigration.Worker -c Release`.

## Steps
Run each command with `dotnet run -c Release --project src/PowerBase.QbMigration.Worker -- <command> --tenant 19 ...`. Every step can be run again safely.

1. **Import the schema:** import the POC app's QBL through the normal import wizard. That creates the PowerBase app `ZZ QB Migration POC`. Note its app id.
2. **Create the state tables:** `init`. This is done in dev already: the `qbm` schema exists in `Powerbase_19`, created 2026-11-03.
3. **Map:** `map --qb-app <dbid> --pb-app <id> --run-as <your user id>`. It prints each table's field actions and records them in `qbm.FieldMap`. A table with any **blocked** field isn't loaded. Fix the cause (usually a skipped or renumbered field in the QBL import), then map again.
4. **Plan:** `plan --run <runId>`. It reads the QuickBase count and the min/max Record ID, writes the chunk ledger, and refuses any destination table that isn't empty.
5. **Load:** `run --run <runId>`. This uses the in-process queue with 2 workers (`Migration:Workers`). Add `--queue servicebus` once the dedicated Service Bus exists.
6. **Users:** `users --run <runId> --app-role <id> --tenant-role <id>` shows who would be created. Add `--apply` to create them inactive, with no emails. Then `run --run <runId> --reload`: it sets the done chunks back to pending and clears their load issues, and each chunk replaces its own rows, so this time the users resolve. (A plain `run` skips done chunks.)

**Picking up changes made in QuickBase after the load** (delta, POC form): `plan --run <runId> --extend` plans chunks only for records created above the planned Record ID ranges and refreshes QuickBase's count; `run --run <runId> --reload` re-reads the loaded ranges, so edits and deletions there are applied too. Then `finalize` and `validate` as usual. Tested 2026-11-04: one Employee added in QuickBase → 1 new chunk, all gates passed. A new QuickBase table needs the QBL update import first, then its own `map`.

First real run (Visitor Management, ysl realm, 2026-11-04): 6 tables, 512 records, all gates passed after `users --apply` and `run --reload`. Found and fixed: QuickBase sent the Record ID# reference as `1.0`.
7. **Finalize:** `finalize --run <runId>`. It sets RecordCount, makes sure new records get ids above the migrated ones, and checks gate 1 (row count) and gate 2 (id range). It exits with code 2 if a gate fails.
8. **Validate:** `validate --run <runId>`. Gate 3 reads QuickBase again and compares **every value of every record** with what PowerBase holds (decrypted). Gate 4 checks that every reference has its parent. Gate 7 checks that encrypted columns really hold ciphertext. It exits with code 2 if a gate fails, and details are in `qbm.Issue` under the `QBM_GATE*` codes. Use `--sample 0.01` on big tables later. **Don't edit the QuickBase app while this runs**: edits show up as differences.
9. **Search:** start the PowerBase API, then `reindex --run <runId> --api http://localhost:5159`. It needs a super admin's user token in user-secrets as `PowerBase:ApiToken`, and it calls the existing `POST admin/search/backfill` for each table.
10. **Report:** `report --run <runId>`, plus `SELECT * FROM qbm.Issue WHERE RunId = <runId>`.

## Built vs still to build in Stage 1
| Item | Status |
|---|---|
| Mapping, transforms, range chunks, page loop, rate limit and retries | built and tested |
| Loader: keeps ids, stable PublicIds, encryption fails closed, replay-safe, one transaction per chunk | built and tested on real SQL |
| Gates 1, 2 and 5 (pending chunks), RecordCount, identity reseed | built (gate 5 = `report` shows no pending chunks) |
| Inactive users without email | built, **not yet run** against real tables |
| Service Bus consumer, ACK after commit, dead-letter on 4xx | built, **not yet run**: needs the dedicated namespace |
| Gate 3 (100% field comparison), gate 4 (reference integrity), gate 7 (no plain text in encrypted columns) | built and tested on real SQL (`validate`) |
| Gate 6 (formula, lookup and summary results) | manual in the POC: compare in the app; PowerBase computes these when you view them |
| Search re-index after load | built (`reindex`, uses the existing admin backfill endpoint), **not yet run** |
| Failure drill: hard-kill the worker mid-load | **passed** 2026-11-04 on Visitor Management: killed during the Users chunk; the uncommitted chunk rolled back (no partial rows); a plain `run` loaded only that chunk; 500 rows / 500 distinct ids; all gates passed |
| Failure drill: drop SQL connections mid-load | **passed, partly**: 9 worker sessions killed during a load, no chunk failed (SqlClient reconnects idle connections). The chunks were too small for a kill to land inside a transaction; repeat at pilot scale |
| Failure drill: QuickBase 429s, poison row → dead-letter | 429 handling covered by unit tests only (not forced against the real realm); dead-letter needs the Service Bus run |
| Regression after the POC fixes | unchanged from Stage 0 (Formula 305/305, Imports 1224/1228 same 4 mail tests, UnitTests 11956 pass / 2 skipped) |

## Assumptions the POC must confirm
The 100% field comparison will prove or disprove each of these:
- QuickBase type names in the compatibility table (`text`, `numeric`, `multitext`, `timestamp`, …).
- How the fields API links Address sub-fields (`properties.compositeFields`, or `parentFieldId` on the sub-field).
- Percent stored ×100 and Duration stored in minutes (`Migration:PercentMultiplier`, `Migration:DurationDivisor`).
- DateTime stored as UTC.
- Custom-key references compare text keys. A numeric custom key, a self-referencing key or an encrypted key is reported, not guessed.
