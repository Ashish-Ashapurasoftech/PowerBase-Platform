# QB → PB migration — Stage 0 baseline (2026-11-03)

The state of the system **before any migration code exists**. Every later stage reruns these commands and must match these numbers. Any new failure is a regression and blocks that stage.

## Code state
| Repo | Branch | Commit | Uncommitted |
|---|---|---|---|
| `C:\PowerBase Dev` (backend) | `prashantrai` | `13fb5f33b21b775cfb39a7d03cd947758589b67c` | 4 local config files: `src/PowerBase.API/appsettings{,.Development}.json`, `src/PowerBase.Migrator/appsettings{,.Development}.json` |
| `C:\PB-Frontend\PB-Frontend` (frontend) | `prashantrai` | `581db165eda052bb99e1e90e2d5b543a6ed9b1ea` | none |

## Test results
| Suite | Result | Known failures (not related to migration) |
|---|---|---|
| `PowerBase.Formula.Tests` | **305 / 305 pass** | none |
| `PowerBase.Imports.Tests` | **1224 / 1228 pass** | 4 mail tests: `ImportMailNoteTests` ×2 and `ImportRunLifecycleTests` ×2. The completion mail line in `ImportRunProcessor` is commented out locally |
| `PowerBase.UnitTests` (Release) | **11956 pass, 0 fail, 2 skipped** | 2 skipped `ReadUncommittedConnectionTests` |
| `PowerBase.IntegrationTests` | **not runnable here**: 3 pass, 103 fail | Every failure is `DockerUnavailableException`. Testcontainers needs Docker, which isn't running on this machine |
| Frontend `ng build` | **pass** | warnings only |
| Frontend `ng test` (vitest, all) | **1689 / 1693 pass** (97 of 100 files) | `pipeline-filter-operator-matrix.spec.ts` reads a missing fixture `C:\API\tests\...\pipeline-filter-operator-matrix.json`; `pb-filter-node.component.spec.ts` ×3 (`isNotCurrentUser` vs `contains`); `pipeline-relative-date.component.spec.ts` ×1 (NG0203 injection context) |
| Frontend `vitest run src/app/core/utils` | **247 / 247 pass** | none |

## Rerun commands
Run these from `C:\PowerBase Dev`. `--artifacts-path` avoids Visual Studio's file locks, and the extra `-p:` works around the SYSLIB1042 generator issue on this machine.
```bash
dotnet test tests/PowerBase.Formula.Tests --artifacts-path <scratch>/artifacts -p:BundledNETCoreAppPackageVersion=8.0.17
dotnet test tests/PowerBase.Imports.Tests --artifacts-path <scratch>/artifacts -p:BundledNETCoreAppPackageVersion=8.0.17
dotnet test tests/PowerBase.UnitTests -c Release --artifacts-path <scratch>/artifacts-rel -p:BundledNETCoreAppPackageVersion=8.0.17
```
Run these from `C:\PB-Frontend\PB-Frontend`:
```bash
npx ng build --output-path <scratch>/fe-dist
npx ng test --watch=false
```

## Things found during Stage 0
1. **A Debug build of `PowerBase.API` changes the database.** `PowerBase.API.csproj:32-38` has a `RunMigrations` target that, after every Debug build, runs `PowerBase.Migrator migrate control` and `migrate tenants` against the configured database. `PowerBase.UnitTests` references the API project, so running the unit tests in Debug migrates the dev DB, and the nested `dotnet run` also crashed the test build (MSB4166).
   - During this baseline it applied `tenant/089_formelement_add_record_picker.sql` to `Powerbase_19` at 2026-11-03 07:29 UTC. That script is a teammate's: idempotent, it adds 3 nullable columns to `meta.FormElement`. A normal Debug run of the API would have applied it too.
   - **Rule for the migration work:** run backend tests with `-c Release`, and the new `PowerBase.QbMigration.Worker` project must **not** reference `PowerBase.API`.
2. **Frontend `node_modules` was out of date.** It was missing `@fontsource-variable/source-serif-4`, which `package.json` and the lockfile declare, and the build failed because of it. `npm ci` fixed it without changing any tracked file.
3. **Secrets in local config files.** The automated review flagged them: a Storage account key, the Service Bus `RootManageSharedAccessKey`, the Azure Search key, and the `sa` password in the 4 modified `appsettings*.json` files. **Rotate these keys** if they were ever committed or shared. Use user-secrets or Key Vault locally. The migration's own resources keep secrets only in their new Key Vault.

## Dev environment for the POC
- SQL Server `DESKTOP-069FFBI`: control DB `Powerbase`, and one tenant, **Pfizer (id 19, `Powerbase_19`, Ready)**.
- Existing apps on the tenant: 1 Pharmsci L&D Connect, 2 LMS, 3 Demo App, 4 Table Level Import App, 5 ZZ Change Type Test, 6 FTC. The POC gets a **new** app, `ZZ QB Migration POC`, so none of these are touched.
- Last applied migrations: tenant `089_formelement_add_record_picker.sql`, control `016_field_snapshot_job.sql`. **Next free numbers: tenant 090, control 017.**
- Docker isn't available, so new real-SQL tests should use the tempdb pattern from `PowerBase.Imports.Tests`, not Testcontainers.

## Stage 0 status
- [x] Test baseline recorded
- [x] POC target confirmed: new scratch app on tenant 19
- [x] POC QuickBase app spec written: `poc-quickbase-app-spec.md`
- [ ] **Team:** build and seed the POC QB app, send the realm, app id, QBL export, user token (securely) and test-user emails
- [ ] **Team:** Azure access to create `rg-pb-qbmigration` (only needed from the first Service Bus test; Stage 1 code and its unit tests don't need it)
