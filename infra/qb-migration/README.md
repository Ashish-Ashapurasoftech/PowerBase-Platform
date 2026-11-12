# QB → PB migration: Azure resources (Stage 1 dev run, then pilot)

Dedicated, temporary resources for the migration worker. Nothing here is shared with the running PowerBase (its App Service, Service Bus and search are left alone). Delete it all when the migration is done.

## What `main.bicep` creates (in a new resource group)
| Resource | Why |
|---|---|
| Key Vault (access policies, 7-day soft delete, no purge protection) | The only place the migration's secrets live |
| Service Bus namespace (Standard) + queue `qb-migration-chunks` (no sessions, lock 5 min, max delivery 5, dead-letter on) | The chunk queue for `run --queue servicebus` |
| Queue rule `worker` (send + listen only), its connection string written straight into the vault | The worker never gets the namespace root key |
| Linux VM `vm-<prefix>-worker` (Ubuntu 22.04, B2s, system-assigned identity, SSH key only, static public IP, daily auto-shutdown) | Runs the worker next to the database |
| Access policy: VM identity → get/list secrets (Option B only) | The worker reads its settings as itself, no stored credentials |
| Access policy: you (or a group) → get/list/set/delete secrets | To load the secrets (and read them when the worker runs on your laptop) |

**Optional, off unless you fill the parameters.** These two touch PowerBase's existing resources, so they need the owner's OK:
- **SQL firewall rule:** a rule on the PowerBase SQL server for the worker's public IP.
- **Read access to PowerBase's Key Vault:** Key Vault Secrets User on the vault behind `KeyVault:Uri`, which holds the tenant connection strings. Needed only if the dev tenants keep their connection strings there. Unlike the rest of the template, this one needs role-assignment rights. Not needed in the current subscription (no PowerBase vault there).

Rough cost while running: the VM is the main cost (B2s), Service Bus Standard has a small monthly base, and the vault and IP cost little. Check the Azure pricing page for your region. The VM stops itself daily at the `autoShutdownTimeUtc` time.

## Option A: no VM (Stage 1, chosen 2026-11-05)
`deployWorkerVm = false` (the default in `main.bicepparam`) creates only the vault, the Service Bus namespace, the queue and its `worker` rule, and your access policy on the vault. Cost is about $10/month (Service Bus Standard base) plus cents for the vault. The worker runs on an operator's laptop:
- **Vault access:** the laptop signs in with `az login` as the person in `vaultOfficerPrincipalId` (that access policy includes read). The worker's DefaultAzureCredential picks up that login.
- **Database access:** the laptop's IP must be allowed on the dev SQL server (likely already true for developers). The optional firewall/PowerBase-vault grants in the template apply only to the VM, so for Option A ask the owner by hand if needed.
- **Settings:** `QBM_Migration__KeyVaultUri=<vaultUri>` in the environment, everything else from the vault. Step 4 (copy to the VM) is skipped. Vault values override the laptop's user-secrets, which still point at the local database. Run the worker locally with `dotnet run -c Release --project src/PowerBase.QbMigration.Worker -- <command>`.

Moving to Option B later: set `deployWorkerVm = true`, fill the SSH key and IP, and deploy again into the same resource group. The vault and queue are kept.

## Who can deploy
Contributor on the subscription or resource group is enough: the vault uses access policies, so no role assignments are made. (The optional PowerBase-vault grant is the exception.)

## Steps

### 1. Deploy
```bash
az group create --name rg-pb-qbmigration --location westcentralus
```
Fill `main.bicepparam` (SSH public key, your IP, the object id from `az ad signed-in-user show --query id -o tsv`), then:
```bash
az deployment group create --resource-group rg-pb-qbmigration --parameters infra/qb-migration/main.bicepparam
```
Note the outputs `vaultName`, `vaultUri`, `workerPublicIp`.

### 2. Load the secrets into the new vault
Type each value into a local file or the terminal, never into chat or a ticket. Secret names use `--` for `:`.

| Secret | Value |
|---|---|
| `ConnectionStrings--DefaultConnection` | The dev PowerBase **control** database connection string |
| `Encryption--MasterKey` | The dev PowerBase encryption master key (same as the dev API's) |
| `QuickBase--RealmHostname` | e.g. `ysl.quickbase.com` |
| `QuickBase--UserToken` | A user token assigned to the app(s) being migrated, view access is enough |
| `KeyVault--Uri` | Only if the dev tenants' connection strings are in PowerBase's vault: that vault's URI |
| `ServiceBus--ConnectionString` | Already written by the template |

```bash
az keyvault secret set --vault-name <vaultName> --name QuickBase--UserToken --file token.txt
```

### 3. Let the worker reach the database
Either fill `sqlServerName`/`sqlServerResourceGroup` and redeploy, or have the SQL server's owner add a firewall rule for `workerPublicIp`. Same for `powerBaseVaultName` if `KeyVault--Uri` is set.

### 4. Copy the worker to the VM
```bash
dotnet publish src/PowerBase.QbMigration.Worker -c Release -r linux-x64 --self-contained true -p:BundledNETCoreAppPackageVersion=8.0.17 -o out/qbm
```
```bash
scp -r out/qbm qbmadmin@<workerPublicIp>:~/qbm
```
On the VM, point the worker at the vault (this is the only setting it needs locally):
```bash
echo 'export QBM_Migration__KeyVaultUri=<vaultUri>' >> ~/.bashrc && source ~/.bashrc && chmod +x ~/qbm/PowerBase.QbMigration.Worker
```

### 5. Run the migration (same steps as locally, `docs/qb-migration/poc-runbook.md`)
First import the app's QBL into the **dev** PowerBase with the import wizard and note the app id and tenant id. Then on the VM:
```bash
cd ~/qbm && ./PowerBase.QbMigration.Worker init --tenant <id>
```
Then `map`, `plan`, `run --queue servicebus`, `users`, `finalize` and `validate`, exactly as in the runbook. With `--queue servicebus`, each chunk is a queue message, and a chunk that fails for good lands in the queue's dead-letter list. Check that list in the portal (Service Bus → queue → Dead-letter) as part of gate 5.

## Teardown (end of the migration)
```bash
az group delete --name rg-pb-qbmigration --yes
```
```bash
az keyvault purge --name <vaultName>
```
Also remove the optional grants if they were added: the SQL firewall rule `qb-migration-worker`, and the worker's role assignment on PowerBase's vault. Then revoke the QuickBase user token.
