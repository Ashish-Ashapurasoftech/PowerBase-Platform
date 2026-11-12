// Lets the migration worker reach PowerBase's Azure SQL server. Remove the rule at teardown.
param sqlServerName string
param ruleName string
param ipAddress string

resource server 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: sqlServerName
}

resource rule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: server
  name: ruleName
  properties: {
    startIpAddress: ipAddress
    endIpAddress: ipAddress
  }
}
