// Filled 2026-11-06 for the Stage 1 run (Option B). No secrets here: the SSH key is public, the rest are names and ids.
using 'main.bicep'

param location = 'westus2'                // same region as powerbase-server-uat
param vaultOfficerPrincipalId = 'cd231f91-a2ae-4953-9d1f-2d739865a92e' // dev@yslworkflow.com

// Option B: worker VM in Azure. Non-burstable size, so speed stays steady on long runs.
param deployWorkerVm = true
param vmSize = 'Standard_D4as_v7'   // 4 vCPU, 16 GB, $0.182/h; older sizes have no quota/capacity in westus2
param diskControllerType = 'NVMe'  // required by v7 sizes
param adminSshPublicKey = 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIPfGfZEevIrWKwmdgKaWbLLV5jglCFD58Mi9WhqBNyOH qb-migration-worker'
param allowedSshSource = '106.215.158.218/32' // office IP; update if it changes
param autoShutdownTimeUtc = '1830'        // 00:00 IST, so an idle VM stops itself

// Not needed: the SQL server already allows Azure services, and tenant connections are not in a vault.
param sqlServerName = ''
param sqlServerResourceGroup = ''
param powerBaseVaultName = ''
param powerBaseVaultResourceGroup = ''
