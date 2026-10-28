// Tells the owner, by email and in the Azure mobile app, when the engine needs attention (pre-live assessment F-01), instead
// of waiting for someone to open the dashboard. Two log search alerts on the engine's console and system logs in Log Analytics
// (agents-platform.bicep), about $3 a month together at a 5-minute frequency:
//   - engine events, one alert per kind: a stop-loss exit blocked or failing, the kill switch engaged (by a safety rule, the
//     reconciliation or the owner's own Pause), drawdown at the hard ceiling, the engine lease lost (the engine stops; also
//     once when a new revision takes over), the safety checks failing, the engine state not saved, the container restarting;
//   - no heartbeat for 15 minutes from a running engine: it stopped, hangs, or the workspace's daily log cap was reached.
// Each fires once and resolves by itself when the condition clears (stateful). Logs are matched by the messages the engine
// writes (its LoggerMessage texts): change those and these queries together. Deployed by infra/main.bicep with the platform,
// when a contact address is given; the engine app is found by name (agents-names.bicep).

import { agentsNames } from 'agents-names.bicep'

param namePrefix string
param environmentName string

@description('The Log Analytics workspace\'s region (the agents\' location): log search alerts live beside it.')
param location string

param tags object

@description('Where the alerts go: email, and push to the Azure mobile app signed in with this address.')
param contactEmail string

@description('Watch the heartbeat: only while the engine runs (agents apps with startEngine), or a stopped engine would alert.')
param watchHeartbeat bool

var names = agentsNames(namePrefix, environmentName, resourceGroup().id)
var app = names.engineApp

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: names.logs
}

resource owner 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${namePrefix}-${environmentName}-owner'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: take('${namePrefix}${environmentName}', 12)
    enabled: true
    emailReceivers: [{ name: 'owner-email', emailAddress: contactEmail, useCommonAlertSchema: true }]
    azureAppPushReceivers: [{ name: 'owner-app', emailAddress: contactEmail }]
  }
}

// One row per matching log line, with the kind of event as a dimension: each kind is its own alert, with its own email.
var eventsQuery = replace('''
union isfuzzy=true
  (ContainerAppConsoleLogs_CL
    | where ContainerAppName_s == '{app}'
    | extend Event = case(
        Log_s contains 'Exit blocked for', 'stop-loss exit blocked',
        Log_s contains 'Stop-loss exit for' and Log_s contains 'failed', 'stop-loss exit failing',
        Log_s contains 'hard ceiling: critical state', 'drawdown at the hard ceiling',
        Log_s contains 'Kill switch engaged by', 'kill switch engaged',
        Log_s contains 'Engine lease lost', 'engine lease lost: the engine stops',
        Log_s contains 'Safety check failed', 'safety checks failing',
        Log_s contains 'engine state file could not be written', 'engine state not saved',
        '')
    | where Event != ''),
  (ContainerAppSystemLogs_CL
    | where ContainerAppName_s == '{app}'
    | extend Reason = tostring(column_ifexists('Reason_s', ''))
    | where Reason in~ ('ContainerTerminated', 'ContainerCrashing', 'ContainerBackOff', 'ProbeFailed')
        or Log_s contains 'ContainerCrashing' or Log_s contains 'ContainerTerminated' or Log_s contains 'BackOff'
    | extend Event = 'engine container restarting')
| project TimeGenerated, Event
''', '{app}', app)

resource events 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: 'alert-${namePrefix}-${environmentName}-engine-events'
  location: location
  tags: tags
  properties: {
    displayName: 'CIX ${environmentName}: engine needs attention'
    description: 'The engine logged a stop-loss exit blocked or failing, a kill switch engagement, the drawdown hard ceiling, a lost lease, failing safety checks or an unsaved state, or its container restarted. The Event dimension says which. Look at the dashboard, then the engine log in Log Analytics.'
    severity: 1
    enabled: true
    scopes: [logs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M' // overlapping windows, so a line that arrives late is still seen; stateful, so no second email
    criteria: {
      allOf: [
        {
          query: eventsQuery
          timeAggregation: 'Count'
          dimensions: [{ name: 'Event', operator: 'Include', values: ['*'] }]
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    skipQueryValidation: true // a new workspace has no engine logs yet, so its tables don't exist when this is created
    actions: { actionGroups: [owner.id] }
  }
}

// A row only when the engine's latest start, heartbeat or standby line is from a running engine (not standby) and over 15
// minutes old: HeartbeatService logs every minute. Two days back at most, the limit for log search alerts, so after two days
// of total silence the alert resolves by itself (it fired long before).
var heartbeatQuery = replace('''
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(2d) and ContainerAppName_s == '{app}'
| where Log_s contains 'Heartbeat (' or Log_s contains 'CIX engine started in' or Log_s contains 'Standing by:'
| summarize arg_max(TimeGenerated, Log_s)
| where isnotnull(TimeGenerated) and Log_s !contains 'Standing by:' and TimeGenerated < ago(15m)
| project TimeGenerated, LastLine = Log_s
''', '{app}', app)

resource heartbeat 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = if (watchHeartbeat) {
  name: 'alert-${namePrefix}-${environmentName}-engine-heartbeat'
  location: location
  tags: tags
  properties: {
    displayName: 'CIX ${environmentName}: no heartbeat from the engine'
    description: 'The running engine has logged no heartbeat for 15 minutes: it stopped or hangs (stops run only while it runs), or the workspace reached its daily log cap. Check the engine\'s revision and its log.'
    severity: 0
    enabled: true
    scopes: [logs.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    overrideQueryTimeRange: 'P2D'
    criteria: {
      allOf: [
        {
          query: heartbeatQuery
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    skipQueryValidation: true
    actions: { actionGroups: [owner.id] }
  }
}
