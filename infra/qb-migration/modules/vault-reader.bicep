// Read-only access for the migration worker to PowerBase's own Key Vault (tenant connection strings, KeyVault:Uri).
// Remove the role assignment at teardown.
param vaultName string
param principalId string

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
