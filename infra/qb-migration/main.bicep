// Dedicated, temporary resources for the QuickBase -> PowerBase migration (docs/qb-migration).
// Deploy into a NEW resource group (rg-pb-qbmigration). deployWorkerVm=false gives vault + Service Bus only (Option A). Nothing here touches PowerBase's running resources, except the
// two optional grants at the bottom (SQL firewall rule, read access to PowerBase's tenant-secrets vault), off by default.
// Delete the resource group when the migration is finished (README.md, "Teardown").

targetScope = 'resourceGroup'

@description('Region. Use the region of the PowerBase SQL server, so the worker and the database are close.')
param location string = resourceGroup().location

@description('Short prefix for resource names (lowercase letters and digits).')
@minLength(3)
@maxLength(12)
param prefix string = 'pbqbmig'

@description('Create the worker VM (and its network, IP, disk). false = vault + Service Bus only; the worker runs on an operator machine (Option A, Stage 1).')
param deployWorkerVm bool = true

@description('Linux admin user on the worker VM.')
param adminUsername string = 'qbmadmin'

@description('SSH public key for the admin user. Password sign-in is disabled. Ignored when deployWorkerVm is false.')
param adminSshPublicKey string = ''

@description('Public IP or CIDR allowed to SSH to the worker VM. Empty = no SSH rule (use "az vm run-command" instead).')
param allowedSshSource string = ''

@description('Worker VM size. B2s is enough for the POC and pilot; raise for the scale test.')
param vmSize string = 'Standard_B2s'

@description('Disk controller. v6/v7 sizes (e.g. Standard_D4as_v7) need NVMe; older sizes use SCSI.')
@allowed([ 'SCSI', 'NVMe' ])
param diskControllerType string = 'SCSI'

@description('Daily auto-shutdown time (UTC, HHmm) so an idle VM does not run up cost. Empty = no auto-shutdown.')
param autoShutdownTimeUtc string = '1830'

@description('Create the private storage account where the worker saves its Excel reports (downloadable from the Portal).')
param deployReportStorage bool = true

@description('Object id of the person or group that will put the secrets into the vault (gets a vault access policy: get, list, set, delete secrets).')
param vaultOfficerPrincipalId string

@description('Optional: name of the PowerBase Azure SQL server, to add a firewall rule for the worker. Empty = skip.')
param sqlServerName string = ''

@description('Resource group of that SQL server.')
param sqlServerResourceGroup string = ''

@description('Optional: name of PowerBase\'s own Key Vault (the KeyVault:Uri vault with tenant connection strings), to let the worker read them. Empty = skip.')
param powerBaseVaultName string = ''

@description('Resource group of that vault.')
param powerBaseVaultResourceGroup string = ''

var suffix = uniqueString(resourceGroup().id)
var vaultName = take('kv-${prefix}-${suffix}', 24)
var serviceBusName = 'sb-${prefix}-${suffix}'
var queueName = 'qb-migration-chunks'
var vmName = 'vm-${prefix}-worker'

// ---------- Key Vault: the only place the migration's secrets live ----------
// Access policies, not Azure RBAC: the deployer only needs Contributor (no role-assignment rights).
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: false
    accessPolicies: [
      {
        // The person who loads the secrets (and, for Option A, runs the worker on a laptop).
        tenantId: subscription().tenantId
        objectId: vaultOfficerPrincipalId
        permissions: { secrets: [ 'get', 'list', 'set', 'delete', 'recover' ] }
      }
    ]
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Purge protection stays off so the vault can be purged at teardown.
    publicNetworkAccess: 'Enabled'
  }
}

// ---------- Service Bus: the chunk queue (no sessions; retries inside the worker, dead-letter for real failures) ----------
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusName
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    minimumTlsVersion: '1.2'
  }
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBus
  name: queueName
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    requiresSession: false
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
  }
}

// Send + listen on the queue only; not the namespace root key.
resource workerRule 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2022-10-01-preview' = {
  parent: queue
  name: 'worker'
  properties: {
    rights: [ 'Send', 'Listen' ]
  }
}

resource serviceBusSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'ServiceBus--ConnectionString'
  properties: {
    value: workerRule.listKeys().primaryConnectionString
  }
}

// ---------- Worker VM (Linux, managed identity, SSH key only) ----------
resource nsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = if (deployWorkerVm) {
  name: 'nsg-${prefix}'
  location: location
  properties: {
    securityRules: empty(allowedSshSource) ? [] : [
      {
        name: 'ssh-from-operator'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: allowedSshSource
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '22'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = if (deployWorkerVm) {
  name: 'vnet-${prefix}'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [ '10.60.0.0/24' ] }
    subnets: [
      {
        name: 'worker'
        properties: {
          addressPrefix: '10.60.0.0/27'
          networkSecurityGroup: { id: nsg.id }
        }
      }
    ]
  }
}

// Static, so the SQL firewall rule keeps matching the worker's outbound address.
resource publicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = if (deployWorkerVm) {
  name: 'pip-${prefix}'
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
  }
}

resource nic 'Microsoft.Network/networkInterfaces@2023-11-01' = if (deployWorkerVm) {
  name: 'nic-${prefix}'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          subnet: { id: vnet!.properties.subnets[0].id }
          privateIPAllocationMethod: 'Dynamic'
          publicIPAddress: { id: publicIp.id }
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2024-03-01' = if (deployWorkerVm) {
  name: vmName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    osProfile: {
      computerName: vmName
      adminUsername: adminUsername
      linuxConfiguration: {
        disablePasswordAuthentication: true
        ssh: {
          publicKeys: [
            {
              path: '/home/${adminUsername}/.ssh/authorized_keys'
              keyData: adminSshPublicKey
            }
          ]
        }
      }
    }
    storageProfile: {
      diskControllerType: diskControllerType
      imageReference: {
        publisher: 'Canonical'
        offer: '0001-com-ubuntu-server-jammy'
        sku: '22_04-lts-gen2'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        managedDisk: { storageAccountType: 'StandardSSD_LRS' }
        deleteOption: 'Delete'
      }
    }
    networkProfile: {
      networkInterfaces: [ { id: nic.id, properties: { deleteOption: 'Delete' } } ]
    }
  }
}

resource shutdown 'Microsoft.DevTestLab/schedules@2018-09-15' = if (deployWorkerVm && !empty(autoShutdownTimeUtc)) {
  name: 'shutdown-computevm-${vmName}'
  location: location
  properties: {
    status: 'Enabled'
    taskType: 'ComputeVmShutdownTask'
    dailyRecurrence: { time: autoShutdownTimeUtc }
    timeZoneId: 'UTC'
    targetResourceId: vm.id
  }
}

// The worker reads its settings from the vault as itself.
resource workerReadsVault 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = if (deployWorkerVm) {
  parent: vault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: vm!.identity.principalId
        permissions: { secrets: [ 'get', 'list' ] }
      }
    ]
  }
}

// ---------- Optional grants on PowerBase's existing resources (off unless the names are given) ----------
module sqlFirewall 'modules/sql-firewall.bicep' = if (deployWorkerVm && !empty(sqlServerName)) {
  name: 'qbmig-sql-firewall'
  scope: resourceGroup(sqlServerResourceGroup)
  params: {
    sqlServerName: sqlServerName
    ruleName: 'qb-migration-worker'
    ipAddress: publicIp!.properties.ipAddress
  }
}

module powerBaseVaultRead 'modules/vault-reader.bicep' = if (deployWorkerVm && !empty(powerBaseVaultName)) {
  name: 'qbmig-powerbase-vault-read'
  scope: resourceGroup(powerBaseVaultResourceGroup)
  params: {
    vaultName: powerBaseVaultName
    principalId: vm!.identity.principalId
  }
}

// ---------- Report storage: private container for the worker's Excel reports ----------
module reportStorage 'modules/report-storage.bicep' = if (deployReportStorage) {
  name: 'qbmig-report-storage'
  params: {
    location: location
    prefix: prefix
    vaultName: vault.name
  }
}

output vaultUri string = vault.properties.vaultUri
output vaultName string = vault.name
output serviceBusNamespace string = serviceBus.name
output queueName string = queue.name
output workerVmName string = deployWorkerVm ? vmName : ''
output workerPublicIp string = deployWorkerVm ? publicIp!.properties.ipAddress : ''
output workerPrincipalId string = deployWorkerVm ? vm!.identity.principalId : ''
output reportStorageAccount string = deployReportStorage ? reportStorage!.outputs.storageAccountName : ''
