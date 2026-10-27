#Requires -Version 5.1
<#
.SYNOPSIS
  One-time setup so the dashboard's owner can send manual buy and sell requests (ADR-0012): a writer identity that can only
  create items in CIX's requests container, and the owner role for your Microsoft account.

.DESCRIPTION
  Creates (or reuses) in Microsoft Entra ID and Cosmos DB:
    1. an app registration and service principal, cix-dashboard-writer-<Environment>, with one client secret (one year);
    2. a Cosmos DB custom role, "CIX dashboard request writer (<Environment>)", allowing only readMetadata and creating
       items, scoped to <database>/requests in the shared account, and assigned to that identity alone;
    3. the dashboard's settings COSMOS_WRITER_TENANT_ID, COSMOS_WRITER_CLIENT_ID and COSMOS_WRITER_SECRET (the secret goes
       to the app through a file only you can read, never on a command line, and is never printed);
    4. with -LiveRequests, a second Cosmos DB custom role, "CIX dashboard live request writer (<Environment>)", with the
       same two actions on the live engine's <live database>/requests only, assigned to the same identity (ADR-0018): the
       dashboard's Pause and Resume buttons then reach the engine that trades real money. Revoke it by deleting that one
       role assignment; paper requests keep working;
    5. with -OwnerAccount, an invitation giving that Microsoft account the dashboard's owner role; once you have accepted
       it, run the script again and it confirms that account as the owner (DASHBOARD_OWNER_USER_ID). The API takes only
       that account as the owner: an invitation link someone else redeemed gives them nothing.

  The identity can't read, change or delete anything, in CIX's database or any other in the shared account; the engine
  checks every request again before acting on it. The secret lives only in the dashboard's server-side settings.

  Everything is checked before anything changes, and each change is confirmed first. Running it again changes nothing that
  is already in place; -RotateSecret replaces the secret (do it before the year is up).
  Needs rights to create app registrations, and to manage Cosmos DB role assignments on the shared account.

.EXAMPLE
  .\tools\azure\setup-dashboard-requests.ps1 -Environment dev -OwnerAccount you@example.com
.EXAMPLE
  .\tools\azure\setup-dashboard-requests.ps1 -Environment dev -RotateSecret
.EXAMPLE
  .\tools\azure\setup-dashboard-requests.ps1 -Environment dev -LiveRequests
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [ValidatePattern('^[^@\s]+@[^@\s]+\.[^@\s]+$')][string]$OwnerAccount,
  [switch]$RotateSecret,
  [switch]$LiveRequests,
  [switch]$Yes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CixAzure.psm1') -Force

$DataActions = @(
  'Microsoft.DocumentDB/databaseAccounts/readMetadata',
  'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create'
)
$CredentialName = 'cix-dashboard-requests'

function Confirm-Step([string]$Question) {
  if ($Yes) { return }
  $answer = Read-Host "    $Question [y/N]"
  if ($answer -notmatch '^(y|yes)$') { Write-CixWarn 'Stopped: not confirmed (type y to go ahead). Nothing more was changed.'; exit 1 }
}

function Write-CixTempJson([object]$Value) {
  # Passed to az as @file: inline JSON doesn't survive Windows PowerShell 5.1's native argument quoting. Holds no secret.
  $path = Join-Path ([IO.Path]::GetTempPath()) "cix-$([guid]::NewGuid().ToString('N')).json"
  [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
  return $path
}

Write-CixStep 'Checking prerequisites'
Assert-CixAzureCli

Write-CixStep "Environment '$Environment'"
$settings = Read-CixEnvironment -Name $Environment
if (-not $settings.deployDashboard) { throw "deployDashboard is false in $($settings.path): there is no dashboard to set up." }
if ($settings.protected) { throw "Environment '$Environment' is protected (live money) and has no dashboard of its own: run this for the environment whose dashboard shows it, with -LiveRequests (ADR-0018)." }
$appName = "stapp-$($settings.namePrefix)-$($settings.environmentName)"
$database = $settings.cosmos.databaseName
$identityName = "cix-dashboard-writer-$Environment"
$roleName = "CIX dashboard request writer ($Environment)"
$relativeScope = "/dbs/$database/colls/requests"
# The live engine's database in the same account (Read-CixEnvironment), for the second role (ADR-0018).
$liveDatabases = @(if ($settings.ContainsKey('liveDatabases')) { $settings.liveDatabases }) # @(): one stays an array, none is empty
$liveDatabase = if ($liveDatabases.Count -eq 1) { [string]$liveDatabases[0] } else { $null }
if ($LiveRequests -and -not $liveDatabase) { throw "-LiveRequests needs exactly one protected environment in this environment's Cosmos DB account; found $($liveDatabases.Count)." }
if ($liveDatabase -and $liveDatabase -eq $database) { throw "The protected environment's database is $database too: refusing." }
$liveRoleName = "CIX dashboard live request writer ($Environment)"
$liveRelativeScope = if ($liveDatabase) { "/dbs/$liveDatabase/colls/requests" } else { $null }
Write-CixInfo "Static Web App:  $appName in $($settings.resourceGroup)"
Write-CixInfo "Identity:        $identityName, may only create items in $database/requests"
if ($LiveRequests) { Write-CixInfo "Live requests:   also in $liveDatabase/requests, the engine that trades real money" }

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
$site = Invoke-CixAz @('staticwebapp', 'show', '--name', $appName, '--resource-group', $settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
if (-not $site) { throw "Static Web App $appName not found in $($settings.resourceGroup); run provision.ps1 first." }
$cosmos = Get-CixCosmosAccount -Settings $settings
$cosmosGroup = $settings.shared.resourceGroup
$fullScope = "$($cosmos.id)$relativeScope"
$inScope = { param($s) "$s" -eq $fullScope -or "$s" -eq $relativeScope }
$liveFullScope = if ($liveRelativeScope) { "$($cosmos.id)$liveRelativeScope" } else { $null }
$inLiveScope = { param($s) [bool]$liveRelativeScope -and ("$s" -eq $liveFullScope -or "$s" -eq $liveRelativeScope) }

# --- Check everything first: nothing is created or changed until a reused identity has passed every check. -----------------
Write-CixStep 'Checking what already exists'
$field = { param($o, $n) if ($o -and $o.PSObject.Properties[$n]) { @($o.$n) } else { @() } } # absent fields are empty, not errors
$me = (Invoke-CixAz @('ad', 'signed-in-user', 'show', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson).id

# --display-name matches by prefix: keep exact names only.
$app = @(Invoke-CixAz @('ad', 'app', 'list', '--display-name', $identityName, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
    Where-Object { $_.PSObject.Properties['displayName'] -and $_.displayName -ceq $identityName })
if ($app.Count -gt 1) { throw "More than one app registration is named $identityName; remove the extras first." }
$app = if ($app.Count -eq 1) { $app[0] } else { $null }
$sp = $null
$ourSecret = $false
$roleAssignments = @()
if ($app) {
  Write-CixInfo "Found $identityName ($($app.appId)); checking it has no other way in and no other rights."
  # One kind of credential only: this script's client secret. No certificates, no federated trust, no other owners.
  $details = Invoke-CixAz @('ad', 'app', 'show', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  if (@(& $field $details 'keyCredentials').Count -gt 0) { throw "$identityName has a certificate, which this script never adds. Remove it in the Azure portal (App registrations, Certificates & secrets) first." }
  $secrets = @(& $field $details 'passwordCredentials')
  $foreign = @($secrets | Where-Object { -not $_.PSObject.Properties['displayName'] -or $_.displayName -cne $CredentialName })
  if ($foreign.Count -gt 0) { throw "$identityName has client secrets this script didn't create ($($foreign.Count)). Remove them in the Azure portal (App registrations, Certificates & secrets) first." }
  if ($secrets.Count -gt 1) { throw "$identityName has $($secrets.Count) client secrets; this script keeps exactly one. Remove the others in the Azure portal (App registrations, Certificates & secrets) first." }
  $ourSecret = $secrets.Count -gt 0
  if (@(Invoke-CixAz @('ad', 'app', 'federated-credential', 'list', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson).Count -gt 0) {
    throw "$identityName trusts external tokens (federated credentials). Remove them first: only the dashboard may sign in as it."
  }
  $otherOwners = @(Invoke-CixAz @('ad', 'app', 'owner', 'list', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson | Where-Object { $_.id -ne $me })
  if ($otherOwners.Count -gt 0) { throw "$identityName has other owners ($(($otherOwners | ForEach-Object { $_.id }) -join ', ')), who could add credentials to it. Refusing." }

  $found = @(Invoke-CixAz @('ad', 'sp', 'list', '--filter', "appId eq '$($app.appId)'", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  if ($found.Count -gt 0) {
    $sp = $found[0]
    $spDetails = Invoke-CixAz @('ad', 'sp', 'show', '--id', $sp.id, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
    foreach ($kind in 'passwordCredentials', 'keyCredentials') {
      if (@(& $field $spDetails $kind).Count -gt 0) { throw "The service principal of $identityName has $kind. Remove them first." }
    }
    $spOwners = @(Invoke-CixAz @('ad', 'sp', 'owner', 'list', '--id', $sp.id, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson | Where-Object { $_.id -ne $me })
    if ($spOwners.Count -gt 0) { throw "The service principal of $identityName has other owners ($(($spOwners | ForEach-Object { $_.id }) -join ', ')). Refusing." }
    foreach ($relation in 'appRoleAssignments', 'memberOf') {
      $held = @(& $field (Invoke-CixAz @('rest', '--method', 'get', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/$relation", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson) 'value')
      if ($held.Count -gt 0) { throw "The service principal of $identityName has $relation (API permissions or directory roles/groups). Remove them first: it may only create requests." }
    }
    $azureRoles = @(Invoke-CixAz @('role', 'assignment', 'list', '--assignee', $sp.id, '--all', '--include-inherited', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
    if ($azureRoles.Count -gt 0) {
      throw "$identityName holds Azure roles ($(($azureRoles | ForEach-Object { "$($_.roleDefinitionName) on $($_.scope)" }) -join '; ')). Remove them first: it needs none."
    }
  }
}
# Every Cosmos DB role assignment in the account: this identity's, and anyone else's on the writer role.
$allAssignments = @(Invoke-CixAz @('cosmosdb', 'sql', 'role', 'assignment', 'list', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
$roleAssignments = @($allAssignments | Where-Object { $sp -and $_.principalId -eq $sp.id }) # @(): one or none stays an array

# An existing Cosmos DB role with either name must allow exactly the two data actions, on its requests container only.
$allRoles = @(Invoke-CixAz @('cosmosdb', 'sql', 'role', 'definition', 'list', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
function Get-CixCheckedRole([string]$Name, [scriptblock]$InScope, [string]$Container) {
  $found = @($allRoles | Where-Object { $_.PSObject.Properties['roleName'] -and $_.roleName -ceq $Name })
  if ($found.Count -gt 1) { throw "More than one Cosmos DB role is named '$Name'; remove the extras first." }
  if ($found.Count -eq 0) { return $null }
  $r = $found[0]
  $p = @(& $field $r 'permissions')
  $actions = @($p | ForEach-Object { & $field $_ 'dataActions' } | Where-Object { $_ }) | Sort-Object
  $notActions = @($p | ForEach-Object { & $field $_ 'notDataActions' } | Where-Object { $_ })
  $scopes = @(& $field $r 'assignableScopes')
  if (($actions -join ',') -ne (($DataActions | Sort-Object) -join ',') -or $notActions.Count -gt 0 -or $scopes.Count -ne 1 -or -not (& $InScope $scopes[0])) {
    throw "The existing Cosmos DB role '$Name' allows more than creating items in $Container. Delete it first (az cosmosdb sql role definition delete --account-name $($cosmos.name) --resource-group $cosmosGroup --id $($r.name))."
  }
  return $r
}
$role = Get-CixCheckedRole $roleName $inScope "$database/requests"
$liveRole = Get-CixCheckedRole $liveRoleName $inLiveScope "$liveDatabase/requests"
$isWriter = { param($a) [bool]$role -and $a.roleDefinitionId -eq $role.id -and (& $inScope $a.scope) }
$isLiveWriter = { param($a) [bool]$liveRole -and $a.roleDefinitionId -eq $liveRole.id -and (& $inLiveScope $a.scope) }
# The identity's only Cosmos DB role assignments may be those roles, each on its own container.
$strays = @($roleAssignments | Where-Object { -not (& $isWriter $_) -and -not (& $isLiveWriter $_) })
if ($strays.Count -gt 0) {
  throw "$identityName holds other Cosmos DB role assignments ($(($strays | ForEach-Object { "$($_.roleDefinitionId) on $($_.scope)" }) -join '; ')). Remove them first (az cosmosdb sql role assignment delete)."
}
# Nobody but this identity may hold either writer role.
foreach ($r in @($role, $liveRole) | Where-Object { $_ }) {
  $others = @($allAssignments | Where-Object { $_.roleDefinitionId -eq $r.id -and (-not $sp -or $_.principalId -ne $sp.id) })
  if ($others.Count -gt 0) {
    throw "The role '$($r.roleName)' is also assigned to $(($others | ForEach-Object { $_.principalId }) -join ', '). Remove those assignments first (az cosmosdb sql role assignment delete): only $identityName may create requests."
  }
}
$writerAssigned = @($roleAssignments | Where-Object { & $isWriter $_ }).Count -gt 0
$liveAssigned = @($roleAssignments | Where-Object { & $isLiveWriter $_ }).Count -gt 0
Write-CixInfo 'All checks passed.'

# --- Make the changes. -------------------------------------------------------------------------------------------------------
Write-CixStep 'App registration and service principal'
if ($app) { Write-CixInfo "Using $identityName ($($app.appId))." }
else {
  Confirm-Step "Create the app registration ${identityName}?"
  $app = Invoke-CixAz @('ad', 'app', 'create', '--display-name', $identityName, '--sign-in-audience', 'AzureADMyOrg', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  Write-CixInfo "Created $identityName ($($app.appId))."
}
if (-not $sp) {
  $sp = Invoke-CixAz @('ad', 'sp', 'create', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  Write-CixInfo 'Created its service principal.'
}

Write-CixStep "Cosmos DB role: create items in $database/requests, nothing else"
if (-not $role) {
  Confirm-Step "Create the Cosmos DB role '$roleName' in $($cosmos.name) (readMetadata and items/create, on $database/requests only)?"
  $file = Write-CixTempJson ([ordered]@{
      RoleName         = $roleName
      Type             = 'CustomRole'
      AssignableScopes = @($relativeScope)
      Permissions      = @(@{ DataActions = $DataActions })
    })
  try { $role = Invoke-CixAz @('cosmosdb', 'sql', 'role', 'definition', 'create', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup, '--body', "@$file", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo 'Created.'
}
else { Write-CixInfo 'Exists, with exactly those two actions on that container.' }

if ($writerAssigned) { Write-CixInfo "Already assigned to $identityName." }
else {
  Confirm-Step "Assign '$roleName' to $identityName on $database/requests?"
  Invoke-CixAz @('cosmosdb', 'sql', 'role', 'assignment', 'create', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup,
    '--role-definition-id', $role.id, '--principal-id', $sp.id, '--scope', $relativeScope, '--only-show-errors', '-o', 'none') | Out-Null
  Write-CixInfo 'Assigned.'
}

if ($LiveRequests) {
  Write-CixStep "Live requests: create items in $liveDatabase/requests, nothing else (ADR-0018)"
  Write-CixWarn 'The dashboard can then pause and resume the engine that trades real money. It still cannot place an order or exceed a risk limit.'
  if (-not $liveRole) {
    Confirm-Step "Create the Cosmos DB role '$liveRoleName' in $($cosmos.name) (readMetadata and items/create, on $liveDatabase/requests only)?"
    $file = Write-CixTempJson ([ordered]@{
        RoleName         = $liveRoleName
        Type             = 'CustomRole'
        AssignableScopes = @($liveRelativeScope)
        Permissions      = @(@{ DataActions = $DataActions })
      })
    try { $liveRole = Invoke-CixAz @('cosmosdb', 'sql', 'role', 'definition', 'create', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup, '--body', "@$file", '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
    if (-not $liveRole -or -not $liveRole.PSObject.Properties['id'] -or -not $liveRole.id) { throw "The Azure CLI returned no id for the new role '$liveRoleName'; nothing was assigned. Run this again." }
    Write-CixInfo 'Created.'
  }
  else { Write-CixInfo 'Exists, with exactly those two actions on that container.' }
  if ($liveAssigned) { Write-CixInfo "Already assigned to $identityName." }
  else {
    Confirm-Step "Assign '$liveRoleName' to $identityName on $liveDatabase/requests?"
    Invoke-CixAz @('cosmosdb', 'sql', 'role', 'assignment', 'create', '--account-name', $cosmos.name, '--resource-group', $cosmosGroup,
      '--role-definition-id', $liveRole.id, '--principal-id', $sp.id, '--scope', $liveRelativeScope, '--only-show-errors', '-o', 'none') | Out-Null
    Write-CixInfo 'Assigned.'
  }
  $shown = (Get-CixStaticSiteSettings -SiteId $site.id)['COSMOS_LIVE_DATABASE']
  if ($shown -ne $liveDatabase) { Write-CixWarn "The dashboard's COSMOS_LIVE_DATABASE is '$shown', not ${liveDatabase}: deploy the dashboard (provision.ps1 or CI) so its live buttons write there." }
  Write-CixInfo "To revoke: delete this one role assignment (az cosmosdb sql role assignment list, then delete); paper requests keep working."
}
elseif ($liveAssigned) { Write-CixInfo "Live requests are allowed ('$liveRoleName' on $liveDatabase/requests); delete that role assignment to revoke them." }

Write-CixStep "Dashboard settings ($appName)"
$current = Get-CixStaticSiteSettings -SiteId $site.id
$configured = $current.ContainsKey('COSMOS_WRITER_SECRET') -and $current['COSMOS_WRITER_SECRET'] -and
  $current.ContainsKey('COSMOS_WRITER_CLIENT_ID') -and $current['COSMOS_WRITER_CLIENT_ID'] -eq $app.appId -and
  $current.ContainsKey('COSMOS_WRITER_TENANT_ID') -and $current['COSMOS_WRITER_TENANT_ID'] -eq $account.tenantId
if ($configured -and $ourSecret -and -not $RotateSecret) { Write-CixInfo 'Already set (pass -RotateSecret to replace the secret).' }
else {
  $what = if ($ourSecret) { 'Replace the client secret of' } else { 'Create a client secret (one year) for' }
  Confirm-Step "$what $identityName and store it in ${appName}'s settings (it is not shown)?"
  # Replaces any earlier secret (no --append): only the new one works from here on.
  $credential = Invoke-CixAz -Quiet @('ad', 'app', 'credential', 'reset', '--id', $app.appId, '--display-name', $CredentialName, '--years', '1', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  try {
    if (-not $credential -or -not $credential.PSObject.Properties['password'] -or -not $credential.password) { throw 'The Azure CLI returned no secret.' }
    $current['COSMOS_WRITER_TENANT_ID'] = $account.tenantId
    $current['COSMOS_WRITER_CLIENT_ID'] = $app.appId
    $current['COSMOS_WRITER_SECRET'] = $credential.password
    # The old secret is already revoked: if this fails, say how to recover (a plain run would think it's all in place).
    try { Set-CixStaticSiteSettings -SiteId $site.id -Settings $current }
    catch { throw "The secret was replaced but couldn't be stored in ${appName}'s settings, so owner requests fail until you run this script again with -RotateSecret." }
  }
  finally { $credential = $null; $current = $null }
  Write-CixInfo "Stored. It expires in a year: run this script with -RotateSecret before $((Get-Date).AddYears(1).ToString('yyyy-MM-dd'))."
}

Write-CixStep 'Owner'
# Every provider (the API lists users only for all of them at once): anyone holding the role counts, whatever their sign-in.
$users = @(Invoke-CixAz @('staticwebapp', 'users', 'list', '--name', $appName, '--resource-group', $settings.resourceGroup, '--authentication-provider', 'all', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
$isAad = { param($u) [bool]($u.PSObject.Properties['provider'] -and "$($u.provider)" -eq 'aad') } # -eq ignores case; missing: not aad
$owners = @($users | Where-Object { $_.PSObject.Properties['roles'] -and (@(@($_.roles) -join ',' -split '[,\s]+') -contains 'owner') })
foreach ($o in $owners) { Write-CixInfo "Has the owner role: $($o.displayName)" }
$pinned = (Get-CixStaticSiteSettings -SiteId $site.id)['DASHBOARD_OWNER_USER_ID']
if (-not $OwnerAccount) {
  if (-not $pinned) { Write-CixWarn 'No confirmed owner yet: run this again with -OwnerAccount <your Microsoft account>.' }
}
else {
  # Anyone else holding the role redeemed a link that wasn't for them, or was added by hand: stop until it's removed.
  $strangers = @($owners | Where-Object { -not $_.PSObject.Properties['displayName'] -or $_.displayName -ne $OwnerAccount -or -not (& $isAad $_) })
  if ($strangers.Count -gt 0) {
    throw "$(($strangers | ForEach-Object { $_.displayName }) -join ', ') also hold(s) the owner role. Remove them first: Azure portal, $appName, Role management. Nothing about the owner was changed."
  }
  $mine = @($owners | Where-Object { $_.displayName -eq $OwnerAccount -and (& $isAad $_) })
  if ($mine.Count -gt 1) { throw "More than one account named $OwnerAccount holds the owner role. Remove the extra ones first: Azure portal, $appName, Role management." }
  if ($mine.Count -eq 0) {
    Confirm-Step "Invite $OwnerAccount to the owner role on ${appName}?"
    $link = Invoke-CixAz -Quiet @('staticwebapp', 'users', 'invite', '--name', $appName, '--resource-group', $settings.resourceGroup,
      '--authentication-provider', 'AAD', '--user-details', $OwnerAccount, '--roles', 'owner', '--domain', $site.defaultHostname,
      '--invitation-expiration-in-hours', '2', '--query', 'invitationUrl', '-o', 'tsv')
    Write-CixInfo 'Open this link yourself within 2 hours, signed in with that Microsoft account, and accept. Don''t share it:'
    Write-CixInfo "  $link"
    Write-CixInfo "Then run this script again with -OwnerAccount $OwnerAccount to confirm you as the owner; until then nobody can send requests."
  }
  elseif (-not $mine[0].PSObject.Properties['userId'] -or -not $mine[0].userId) { throw "Couldn't read the user ID of $OwnerAccount from the Static Web App." }
  elseif ($pinned -eq $mine[0].userId) { Write-CixInfo "$OwnerAccount is the confirmed owner." }
  else {
    Confirm-Step "Confirm $OwnerAccount as the dashboard's owner (only that account can send requests)?"
    $current = Get-CixStaticSiteSettings -SiteId $site.id
    $current['DASHBOARD_OWNER_USER_ID'] = $mine[0].userId
    try { Set-CixStaticSiteSettings -SiteId $site.id -Settings $current } finally { $current = $null }
    Write-CixInfo "Confirmed. Sign in on the dashboard and the Buy and Sell buttons work (docs/azure.md, ""Owner requests"")."
  }
}
