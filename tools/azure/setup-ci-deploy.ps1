#Requires -Version 5.1
<#
.SYNOPSIS
  One-time setup so CI can publish the dashboard without any secret stored in GitHub (ADR-0010).

.DESCRIPTION
  Creates (or reuses) in Microsoft Entra ID and Azure:
    1. an app registration and service principal, cix-dashboard-deploy-<Environment>;
    2. a federated credential that trusts GitHub Actions tokens only for pushes to main of this repository
       (subject repo:<owner>/<repo>:ref:refs/heads/main): pull requests, other branches and forks can't use it;
    3. a custom role, "CIX dashboard deployer (<Environment>)", that may only read a Static Web App and its deployment
       token, assigned on this environment's Static Web App alone.
  Then prints the three repository variables CI needs (IDs, not secrets), and sets them with the GitHub CLI if you pass
  -SetGitHubVariables and gh is installed and signed in.

  Each change is shown and confirmed first. Running it again changes nothing that is already in place.
  Needs rights to create app registrations and role assignments (Owner on the subscription is enough).

.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id>
.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id> -SetGitHubVariables
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
  [switch]$SetGitHubVariables,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

function Confirm-Step([string]$Question) {
  if ($Yes) { return }
  $answer = Read-Host "    $Question [y/N]"
  if ($answer -notmatch '^(y|yes)$') { Write-CixWarn 'Stopped: not confirmed (type y to go ahead). Nothing more was changed.'; exit 1 }
}

function Write-CixTempJson([object]$Value) {
  # Passed to az as @file: inline JSON doesn't survive Windows PowerShell 5.1's native argument quoting.
  $path = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N')).json"
  [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
  return $path
}

Write-CixStep 'Checking prerequisites'
Assert-CixAzureCli
if (-not $Repository) {
  $remote = Invoke-CixNative { & git -C (Get-CixRepoRoot) remote get-url origin 2>&1 }
  if ($LASTEXITCODE -ne 0 -or "$remote" -notmatch 'github\.com[:/]([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(\.git)?$') {
    throw "Couldn't tell the GitHub repository from git's origin; pass -Repository owner/repo."
  }
  $Repository = $Matches[1]
}
# Entra matches the subject case-sensitively: use GitHub's own spelling of the name when gh is signed in.
if (Get-Command gh -ErrorAction SilentlyContinue) {
  $canonical = Invoke-CixNative { & gh repo view $Repository --json nameWithOwner --jq .nameWithOwner 2>$null }
  if ($LASTEXITCODE -eq 0 -and "$canonical" -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -and "$canonical" -cne $Repository) {
    Write-CixWarn "Using GitHub's spelling $canonical instead of $Repository (the trust is case-sensitive)."
    $Repository = "$canonical"
  }
}
$subject = "repo:${Repository}:ref:refs/heads/main"

Write-CixStep "Environment '$Environment'"
$settings = Read-CixEnvironment -Name $Environment
$appName = "stapp-$($settings.namePrefix)-$($settings.environmentName)"
$identityName = "cix-dashboard-deploy-$Environment"
$roleName = "CIX dashboard deployer ($Environment)"
Write-CixInfo "Static Web App:  $appName in $($settings.resourceGroup)"
Write-CixInfo "Identity:        $identityName, trusted only for $subject"

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
$site = Invoke-CixAz @('staticwebapp', 'show', '--name', $appName, '--resource-group', $settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
if (-not $site) { throw "Static Web App $appName not found in $($settings.resourceGroup); run provision.ps1 first." }

# --- Check everything first: nothing is created or changed until a reused identity has passed every check. -----------------
Write-CixStep 'Checking what already exists'
$field = { param($o, $n) if ($o -and $o.PSObject.Properties[$n]) { @($o.$n) } else { @() } } # absent fields are empty, not errors
$groupId = "/subscriptions/$($account.id)/resourceGroups/$($settings.resourceGroup)"
$me = (Invoke-CixAz @('ad', 'signed-in-user', 'show', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson).id

$app = @(Invoke-CixAz @('ad', 'app', 'list', '--display-name', $identityName, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
if ($app.Count -gt 1) { throw "More than one app registration is named $identityName; remove the extras first." }
$app = if ($app.Count -eq 1) { $app[0] } else { $null }
$sp = $null
$credentials = @()
if ($app) {
  Write-CixInfo "Found $identityName ($($app.appId)); checking it has no other way in."
  # No client secret or certificate, and no owner but you, on the app registration...
  $details = Invoke-CixAz @('ad', 'app', 'show', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  foreach ($kind in 'passwordCredentials', 'keyCredentials') {
    if (@(& $field $details $kind).Count -gt 0) {
      throw "$identityName has $kind (a client secret or certificate), which could sign in from anywhere. Remove them in the Azure portal (App registrations, Certificates & secrets) first."
    }
  }
  $otherOwners = @(Invoke-CixAz @('ad', 'app', 'owner', 'list', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson | Where-Object { $_.id -ne $me })
  if ($otherOwners.Count -gt 0) { throw "$identityName has other owners ($(($otherOwners | ForEach-Object { $_.id }) -join ', ')), who could add credentials to it. Refusing." }

  # ...nor on its service principal, which also holds no directory roles or API permissions...
  $found = @(Invoke-CixAz @('ad', 'sp', 'list', '--filter', "appId eq '$($app.appId)'", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  if ($found.Count -gt 0) {
    $sp = $found[0]
    $spDetails = Invoke-CixAz @('ad', 'sp', 'show', '--id', $sp.id, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
    foreach ($kind in 'passwordCredentials', 'keyCredentials') {
      if (@(& $field $spDetails $kind).Count -gt 0) { throw "The service principal of $identityName has $kind, which could sign in from anywhere. Remove them first." }
    }
    $spOwners = @(Invoke-CixAz @('ad', 'sp', 'owner', 'list', '--id', $sp.id, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson | Where-Object { $_.id -ne $me })
    if ($spOwners.Count -gt 0) { throw "The service principal of $identityName has other owners ($(($spOwners | ForEach-Object { $_.id }) -join ', ')). Refusing." }
    foreach ($relation in 'appRoleAssignments', 'memberOf') {
      $held = @(& $field (Invoke-CixAz @('rest', '--method', 'get', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/$relation", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson) 'value')
      if ($held.Count -gt 0) { throw "The service principal of $identityName has $relation (API permissions or directory roles/groups). Remove them first: it may only read this Static Web App." }
    }
    # ...and no Azure role but ours, on this Static Web App.
    $strays = @(Invoke-CixAz @('role', 'assignment', 'list', '--assignee', $sp.id, '--all', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
        Where-Object { $_.roleDefinitionName -ne $roleName -or $_.scope -ne $site.id })
    if ($strays.Count -gt 0) {
      throw "$identityName also holds $(($strays | ForEach-Object { "$($_.roleDefinitionName) on $($_.scope)" }) -join '; '). Remove those role assignments first: it may only read this Static Web App."
    }
  }

  # It may trust GitHub Actions on main of this repository, and nothing else.
  $credentials = @(Invoke-CixAz @('ad', 'app', 'federated-credential', 'list', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  $others = @($credentials | Where-Object {
      $_.subject -ne $subject -or $_.issuer -ne 'https://token.actions.githubusercontent.com' -or
      (@(& $field $_ 'audiences') -join ',') -ne 'api://AzureADTokenExchange' })
  if ($others.Count -gt 0) {
    throw "$identityName also trusts $(($others | ForEach-Object { "$($_.subject) (issuer $($_.issuer))" }) -join ', '). Remove those in the Azure portal (App registrations, Certificates & secrets) first: only main of $Repository may deploy."
  }
}

# An existing role with this name must grant exactly the two actions, on this resource group only.
$role = @(Invoke-CixAz @('role', 'definition', 'list', '--name', $roleName, '--custom-role-only', 'true', '--scope', $groupId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
if ($role.Count -gt 0) {
  $p = @(& $field $role[0] 'permissions')
  $actions = @($p | ForEach-Object { & $field $_ 'actions' } | Where-Object { $_ }) | Sort-Object
  $extra = @($p | ForEach-Object { @(& $field $_ 'notActions') + @(& $field $_ 'dataActions') + @(& $field $_ 'notDataActions') } | Where-Object { $_ })
  if (($actions -join ',') -ne 'Microsoft.Web/staticSites/listSecrets/action,Microsoft.Web/staticSites/read' -or $extra.Count -gt 0 -or
      ((& $field $role[0] 'assignableScopes') -join ',') -ne $groupId) {
    throw "The existing role '$roleName' grants more than reading this Static Web App and its deployment token. Delete it first (az role definition delete --name `"$roleName`")."
  }
}
Write-CixInfo 'All checks passed.'

# --- Make the changes, the trust for main last. ----------------------------------------------------------------------------
Write-CixStep 'App registration and service principal'
if ($app) { Write-CixInfo "Using $identityName ($($app.appId))." }
else {
  Confirm-Step "Create the app registration ${identityName}?"
  $app = Invoke-CixAz @('ad', 'app', 'create', '--display-name', $identityName, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  Write-CixInfo "Created $identityName ($($app.appId))."
}
if (-not $sp) {
  $sp = Invoke-CixAz @('ad', 'sp', 'create', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  Write-CixInfo 'Created its service principal.'
}

Write-CixStep 'Role: read this Static Web App and its deployment token, nothing else'
if ($role.Count -eq 0) {
  Confirm-Step "Create the custom role '$roleName' (Microsoft.Web/staticSites read and listSecrets only)?"
  $file = Write-CixTempJson ([ordered]@{
      Name             = $roleName
      Description      = 'Reads one CIX Static Web App and its deployment token, to publish the dashboard from CI.'
      Actions          = @('Microsoft.Web/staticSites/read', 'Microsoft.Web/staticSites/listSecrets/action')
      NotActions       = @()
      AssignableScopes = @($groupId)
    })
  try { Invoke-CixAz @('role', 'definition', 'create', '--role-definition', "@$file", '--only-show-errors', '-o', 'none') | Out-Null }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo 'Created.'
}
else { Write-CixInfo 'Exists, with exactly those two actions.' }

$assigned = @(Invoke-CixAz @('role', 'assignment', 'list', '--assignee', $sp.id, '--scope', $site.id, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
    Where-Object { $_.roleDefinitionName -eq $roleName })
if ($assigned.Count -gt 0) { Write-CixInfo "Already assigned on $appName." }
else {
  Confirm-Step "Assign '$roleName' to $identityName on $appName only?"
  # A new role definition can take a minute to become assignable.
  for ($attempt = 1; ; $attempt++) {
    try {
      Invoke-CixAz @('role', 'assignment', 'create', '--assignee-object-id', $sp.id, '--assignee-principal-type', 'ServicePrincipal',
        '--role', $roleName, '--scope', $site.id, '--only-show-errors', '-o', 'none') | Out-Null
      break
    }
    catch {
      if ($attempt -ge 6) { throw }
      Write-CixInfo 'Waiting for the new role to become assignable...'
      Start-Sleep -Seconds 20
    }
  }
  Write-CixInfo 'Assigned.'
}

Write-CixStep 'Federated credential (GitHub Actions, main only)'
if (@($credentials | Where-Object { $_.subject -eq $subject }).Count -eq 1) { Write-CixInfo "Already trusts $subject." }
else {
  Confirm-Step "Let GitHub Actions on main of $Repository sign in as ${identityName}?"
  $file = Write-CixTempJson ([ordered]@{
      name        = 'github-main'
      issuer      = 'https://token.actions.githubusercontent.com'
      subject     = $subject
      audiences   = @('api://AzureADTokenExchange')
      description = 'CI publishes the CIX dashboard from main (tools/azure/setup-ci-deploy.ps1).'
    })
  try { Invoke-CixAz @('ad', 'app', 'federated-credential', 'create', '--id', $app.appId, '--parameters', "@$file", '--only-show-errors', '-o', 'none') | Out-Null }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo "Trusts $subject."
}

Write-CixStep 'GitHub repository variables (IDs, not secrets)'
$variables = [ordered]@{ AZURE_CLIENT_ID = $app.appId; AZURE_TENANT_ID = $account.tenantId; AZURE_SUBSCRIPTION_ID = $account.id; DASHBOARD_ENVIRONMENT = $Environment }
if ($SetGitHubVariables) {
  Assert-CixCommand -Name 'gh' -Hint 'Install the GitHub CLI (winget install GitHub.cli) and run gh auth login, or set the variables by hand.'
  foreach ($k in $variables.Keys) {
    Invoke-CixNative { & gh variable set $k --repo $Repository --body $variables[$k] } | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh variable set $k failed." }
    Write-CixInfo "Set $k."
  }
}
else {
  Write-CixInfo "Set these in GitHub: $Repository, Settings, Secrets and variables, Actions, Variables:"
  foreach ($k in $variables.Keys) { Write-CixInfo ("  {0} = {1}" -f $k, $variables[$k]) }
  Write-CixInfo 'Or run this script again with -SetGitHubVariables (needs the GitHub CLI, signed in).'
}
Write-CixInfo 'Then run CI on main by hand once (Actions, CI, Run workflow) to check it publishes.'
