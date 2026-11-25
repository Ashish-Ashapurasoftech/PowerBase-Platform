<#
  Scale rules and build settings for the ComputeWorker Container App (stored c_ columns). A TEMPLATE for the UAT "C" setup: it has not
  been run from here (no Azure credentials on the dev machine) — read it, then run it in Azure Cloud Shell or a signed-in `az` session.

  What it sets
    * Replicas 1..MaxReplicas. Replicas build DIFFERENT fields at the same time (a field is locked to one replica while it is built) and
      share the database-wide session slots (Compute__MaxSessionsPerDatabase), so more replicas = more CPU for the C# part of a build
      (formulas, read-code fields, decrypting), not more database sessions.
    * Scale OUT while there is something to build: a KEDA "mssql" rule counts the fields of the data database that are switched on but
      not Ready yet (MaterializeMode = 1, MaterializeState 0/1), one replica per FieldsPerReplica fields, plus a CPU rule for the C# load.
    * Scale IN by itself: when the backlog is empty and CPU is low the Container App goes back to MinReplicas after the cool-down
      (default 300 s). Nothing to switch off by hand after a build.
    * Build settings as environment variables (no image change needed).

  Prerequisites: the worker image with checkpoint support (migration 100 applied to the tenant database first: dotnet run --project
  src/PowerBase.Migrator -- migrate tenant --id <N>), and a secret on the Container App holding the ADO.NET connection string of the
  tenant database (read-only is enough: the rule only runs a COUNT).
#>
param(
    [string]$ResourceGroup = 'C-PowerBase-RG',
    [string]$App = 'c-powerbase-compute',
    [int]$MinReplicas = 1,
    [int]$MaxReplicas = 6,
    [int]$FieldsPerReplica = 25,
    [string]$SqlSecretName = 'tenant-sql-conn',   # Container App secret with the tenant database connection string
    # Database-wide worker sessions across ALL replicas (slot 1 stays free for live changes). Raise together with the database tier:
    # Powerbase_17 is General Purpose 14 vCores; log/data IO of that tier is the limit long before the worker's CPU is.
    [int]$MaxSessionsPerDatabase = 16,
    [int]$BackgroundSessionsPerDatabase = 15,
    [int]$BuildsPerReplica = 6,         # fields one replica builds at once
    [int]$SlicesPerField = 4,           # Id ranges a big field is built on side by side
    [int]$ProjectedBatchSize = 1000,
    [switch]$WhatIf
)

$settings = @(
    "Compute__MaxSessionsPerDatabase=$MaxSessionsPerDatabase",
    "Compute__BackgroundSessionsPerDatabase=$BackgroundSessionsPerDatabase",
    "Compute__BuildsPerReplica=$BuildsPerReplica",
    "Compute__SlicesPerField=$SlicesPerField",
    "Compute__SliceMinRows=50000",
    "Compute__ProjectedBatchSize=$ProjectedBatchSize",
    "Compute__PauseBetweenBatchesMs=0",
    "Compute__BuildRoundMinutes=60",
    # the nightly check waits until the backlog is built (default), and skips fields built/checked in the last 20 h
    "Compute__ReconcileWhileBuilding=false",
    "Compute__ReconcileMinAgeHours=20"
)

$commands = @(
    "az containerapp update -g $ResourceGroup -n $App --min-replicas $MinReplicas --max-replicas $MaxReplicas --set-env-vars $($settings -join ' ')",
    # CPU rule: the C# part of a build (formula engine, decrypt, read code) is CPU bound inside the worker.
    "az containerapp update -g $ResourceGroup -n $App --scale-rule-name build-cpu --scale-rule-type cpu --scale-rule-metadata type=Utilization value=60",
    # Backlog rule: one replica per $FieldsPerReplica fields still waiting for their first build (scale-in follows when it is 0).
    "az containerapp update -g $ResourceGroup -n $App --scale-rule-name build-backlog --scale-rule-type mssql " +
        "--scale-rule-metadata `"query=SELECT COUNT(*) FROM meta.AppField WHERE MaterializeMode = 1 AND MaterializeState IN (0, 1) AND IsDeleted = 0`" targetValue=$FieldsPerReplica activationTargetValue=1 " +
        "--scale-rule-auth connectionString=$SqlSecretName"
)

foreach ($c in $commands) {
    Write-Host $c -ForegroundColor Cyan
    if (-not $WhatIf) { Invoke-Expression $c }
}
if ($WhatIf) { Write-Host "`n(-WhatIf: nothing was run)" }
