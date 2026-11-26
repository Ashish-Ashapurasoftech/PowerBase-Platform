// Azure Monitor autoscale for the PowerBase.API App Service plan (doc 19 / 36). Deploys ONLY the autoscale setting: the plan and the
// web app are existing resources and are not modified here (their settings are applied by infra/apply-api-loadbalancing.ps1, which
// patches only the named settings). Review with:
//   az deployment group what-if -g <rg> -f infra/api-autoscale.bicep -p planName=<plan> notificationEmail=<you>
// Prerequisites before minInstances >= 2: the ARC-01 build (shared cache versions, tenant migration 104 + control migration 019 applied)
// and the multi-instance tests of doc 35 passed. Instance limits must respect SQL capacity (doc 21).

@description('Name of the EXISTING App Service plan that hosts PowerBase.API (Premium v3 recommended).')
param planName string

@description('Region of the plan.')
param location string = resourceGroup().location

param minInstances int = 2
param maxInstances int = 6
@description('Instances when the autoscale engine has no metric data.')
param defaultInstances int = 2

param cpuScaleOutPercent int = 65
param cpuScaleInPercent int = 30
param httpQueueScaleOut int = 50

@description('Weekday business-hours profile: at least this many instances (0 = no business-hours profile).')
param businessHoursMinInstances int = 3
@description('Time zone of the business-hours profile (Windows time zone id).')
param businessHoursTimeZone string = 'India Standard Time'
param businessHoursStart string = '08:00'
param businessHoursEnd string = '19:00'

@description('Optional e-mail for scale notifications.')
param notificationEmail string = ''

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: planName
}

var scaleOutCpu = {
  metricTrigger: {
    metricName: 'CpuPercentage'
    metricResourceUri: plan.id
    timeGrain: 'PT1M'
    statistic: 'Average'
    timeWindow: 'PT5M'
    timeAggregation: 'Average'
    operator: 'GreaterThan'
    threshold: cpuScaleOutPercent
  }
  scaleAction: { direction: 'Increase', type: 'ChangeCount', value: '1', cooldown: 'PT10M' }
}
var scaleOutQueue = {
  metricTrigger: {
    metricName: 'HttpQueueLength'
    metricResourceUri: plan.id
    timeGrain: 'PT1M'
    statistic: 'Average'
    timeWindow: 'PT5M'
    timeAggregation: 'Average'
    operator: 'GreaterThan'
    threshold: httpQueueScaleOut
  }
  scaleAction: { direction: 'Increase', type: 'ChangeCount', value: '1', cooldown: 'PT10M' }
}
var scaleInCpu = {
  metricTrigger: {
    metricName: 'CpuPercentage'
    metricResourceUri: plan.id
    timeGrain: 'PT1M'
    statistic: 'Average'
    timeWindow: 'PT15M'
    timeAggregation: 'Average'
    operator: 'LessThan'
    threshold: cpuScaleInPercent
  }
  scaleAction: { direction: 'Decrease', type: 'ChangeCount', value: '1', cooldown: 'PT15M' }
}

var normalProfile = {
  name: 'normal'
  capacity: { minimum: string(minInstances), maximum: string(maxInstances), default: string(defaultInstances) }
  rules: [ scaleOutCpu, scaleOutQueue, scaleInCpu ]
}

var businessHoursProfile = {
  name: 'business-hours'
  capacity: { minimum: string(businessHoursMinInstances), maximum: string(maxInstances), default: string(businessHoursMinInstances) }
  rules: [ scaleOutCpu, scaleOutQueue, scaleInCpu ]
  recurrence: {
    frequency: 'Week'
    schedule: {
      timeZone: businessHoursTimeZone
      days: [ 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday' ]
      hours: [ int(split(businessHoursStart, ':')[0]) ]
      minutes: [ int(split(businessHoursStart, ':')[1]) ]
    }
  }
}

// A recurring profile runs until the next profile starts: this one hands back to "normal" at the end of business hours.
var afterHoursProfile = {
  name: '{"name":"normal","for":"business-hours"}'
  capacity: normalProfile.capacity
  rules: normalProfile.rules
  recurrence: {
    frequency: 'Week'
    schedule: {
      timeZone: businessHoursTimeZone
      days: [ 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday' ]
      hours: [ int(split(businessHoursEnd, ':')[0]) ]
      minutes: [ int(split(businessHoursEnd, ':')[1]) ]
    }
  }
}

resource autoscale 'Microsoft.Insights/autoscalesettings@2022-10-01' = {
  name: '${planName}-autoscale'
  location: location
  properties: {
    enabled: true
    targetResourceUri: plan.id
    profiles: businessHoursMinInstances > 0 ? [ normalProfile, businessHoursProfile, afterHoursProfile ] : [ normalProfile ]
    notifications: empty(notificationEmail) ? [] : [
      {
        operation: 'Scale'
        email: { sendToSubscriptionAdministrator: false, sendToSubscriptionCoAdministrators: false, customEmails: [ notificationEmail ] }
      }
    ]
  }
}

output autoscaleSettingId string = autoscale.id
