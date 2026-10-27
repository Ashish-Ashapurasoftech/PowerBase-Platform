// Alert rules for the search pipeline. A TEMPLATE: it has not been deployed or validated against a subscription, so review the names,
// thresholds and action group before use. Deploy to the resource group that holds the Service Bus namespace and the search service:
//   az deployment group what-if -g <rg> -f infra/search-alerts.bicep -p serviceBusNamespaceName=<ns> searchServiceName=<search> actionGroupId=<id>
// (what-if only previews; run "create" instead of "what-if" to apply.)

@description('Name of the Service Bus namespace.')
param serviceBusNamespaceName string

@description('Name of the Azure AI Search service.')
param searchServiceName string

@description('Resource id of the action group that gets notified (email, Teams, pager).')
param actionGroupId string

@description('Active messages in the indexing queue above which to alert.')
param backlogThreshold int = 5000

var location = 'global'

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: searchServiceName
}

// The indexer is not keeping up, or has stopped.
resource backlog 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'pb-search-queue-backlog'
  location: location
  properties: {
    description: 'Messages are piling up in the search indexing queues.'
    severity: 2
    enabled: true
    scopes: [ serviceBus.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ActiveMessages'
          metricName: 'ActiveMessages'
          operator: 'GreaterThan'
          threshold: backlogThreshold
          timeAggregation: 'Maximum'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupId } ]
  }
}

// Messages the indexer gave up on or Azure rejected: read the dead-letter reason.
resource deadLetters 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'pb-search-dead-letters'
  location: location
  properties: {
    description: 'Messages were moved to the dead-letter queue.'
    severity: 2
    enabled: true
    scopes: [ serviceBus.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'DeadletteredMessages'
          metricName: 'DeadletteredMessages'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Maximum'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupId } ]
  }
}

// Service Bus is refusing requests (Standard tier credit throttling).
resource busThrottling 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'pb-search-bus-throttled'
  location: location
  properties: {
    description: 'Service Bus is throttling requests.'
    severity: 3
    enabled: true
    scopes: [ serviceBus.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ThrottledRequests'
          metricName: 'ThrottledRequests'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupId } ]
  }
}

// The search service is throttling queries (HTTP 503): scale replicas or reduce load.
resource searchThrottling 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'pb-search-throttled-queries'
  location: location
  properties: {
    description: 'Azure AI Search is throttling queries.'
    severity: 3
    enabled: true
    scopes: [ search.id ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'ThrottledSearchQueriesPercentage'
          metricName: 'ThrottledSearchQueriesPercentage'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [ { actionGroupId: actionGroupId } ]
  }
}

// Also create (in Application Insights): an availability test on GET /health/search (alert on any 503), and a log alert on traces containing
// "Search outbox: " warnings ("daily check", "waiting") for changes that are not reaching the index.
