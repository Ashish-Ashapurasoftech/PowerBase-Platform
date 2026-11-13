// Private blob container for the migration's Excel reports. The worker uploads each report here; an operator downloads it
// from the Azure Portal (Storage account > Containers > reports). The connection string goes straight into the migration's
// Key Vault as Migration--ReportStorage--ConnectionString, so it never appears in a file or a deployment output.
// Shared key + vault secret instead of a role assignment: the deployer only needs Contributor.

@description('Region.')
param location string

@description('Name prefix, as in main.bicep.')
param prefix string

@description('The migration Key Vault (created by main.bicep) that receives the connection string.')
param vaultName string

@description('Write the connection string into the vault. false = an operator adds the secret by hand (Portal: Storage account > Access keys).')
param writeConnectionSecret bool = true

@description('Container for the reports.')
param containerName string = 'reports'

var storageName = take('st${prefix}${uniqueString(resourceGroup().id)}', 24)

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false   // reports hold user emails: never anonymous
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: containerName
  properties: { publicAccess: 'None' }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource connectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (writeConnectionSecret) {
  parent: vault
  name: 'Migration--ReportStorage--ConnectionString'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  }
}

output storageAccountName string = storage.name
output containerName string = container.name
