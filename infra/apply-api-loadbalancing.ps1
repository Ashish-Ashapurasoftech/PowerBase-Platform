<#
  App Service settings that make PowerBase.API safe and efficient behind App Service's built-in load balancer (docs 18 / 36).
  PRINTS the az commands by default. Nothing changes in Azure unless you pass -Apply (run it in a signed-in `az` session or
  Cloud Shell, after reviewing the output). Every command patches only the settings it names; no other setting is touched.

  What it sets
    * Always On                       instances never unload after idle (no cold first request)
    * ARR affinity OFF                no sticky sessions: requests spread over every instance (the API keeps no session state)
    * HTTP/2, TLS 1.2 minimum, FTPS off
    * Health check /health/ready      an instance gets traffic only once started and its control DB answers; one failing
                                      WEBSITE_HEALTHCHECK_MAXPINGFAILURES pings in a row (here 3, one ping per minute) is taken
                                      out of rotation and later replaced
    * Deployment slot "staging"       deploy there, warm it, then swap (zero downtime, instant rollback by swapping back)
    * Optional: allow traffic only from Azure Front Door (-FrontDoorId)

  Prerequisites before running with more than one instance: the ARC-01 build deployed and migrations applied
  (tenant 104_create_cache_version.sql on every tenant DB, control 019_create_cache_version.sql), see doc 35.
#>
param(
    [Parameter(Mandatory)] [string]$ResourceGroup,
    [Parameter(Mandatory)] [string]$WebApp,
    [string]$Slot = 'staging',
    [int]$MaxPingFailures = 3,
    [string]$FrontDoorId = '',
    [switch]$Apply
)

$commands = @(
    "az webapp config set -g $ResourceGroup -n $WebApp --always-on true --http20-enabled true --min-tls-version 1.2 --ftps-state Disabled",
    "az webapp update -g $ResourceGroup -n $WebApp --client-affinity-enabled false",
    "az webapp config set -g $ResourceGroup -n $WebApp --generic-configurations '{""healthCheckPath"": ""/health/ready""}'",
    "az webapp config appsettings set -g $ResourceGroup -n $WebApp --settings WEBSITE_HEALTHCHECK_MAXPINGFAILURES=$MaxPingFailures",
    "az webapp deployment slot create -g $ResourceGroup -n $WebApp --slot $Slot --configuration-source $WebApp",
    "az webapp config set -g $ResourceGroup -n $WebApp --slot $Slot --always-on true --generic-configurations '{""healthCheckPath"": ""/health/ready""}'",
    "az webapp update -g $ResourceGroup -n $WebApp --slot $Slot --client-affinity-enabled false"
)
if ($FrontDoorId) {
    $commands += "az webapp config access-restriction add -g $ResourceGroup -n $WebApp --rule-name AllowFrontDoor --priority 100 --service-tag AzureFrontDoor.Backend --http-header x-azure-fdid=$FrontDoorId"
    $commands += "az webapp config access-restriction set -g $ResourceGroup -n $WebApp --default-action Deny --use-same-restrictions-for-scm-site false"
}

foreach ($c in $commands) {
    if ($Apply) {
        Write-Host ">> $c"
        Invoke-Expression $c
        if ($LASTEXITCODE -ne 0) { throw "Failed: $c" }
    } else {
        Write-Host $c
    }
}
if (-not $Apply) { Write-Host "`n(dry run: nothing changed. Review, then run again with -Apply.)" }
