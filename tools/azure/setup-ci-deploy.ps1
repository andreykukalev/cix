#Requires -Version 5.1
<#
.SYNOPSIS
  One-time setup so CI can deploy from main without any secret stored in GitHub (ADR-0010): the dashboard (-For
  dashboard, the default) or the engine and the agent jobs (-For agents). For the protected (live-money) environment,
  -For agents sets up the live deploy, which runs only after the owner approves it in GitHub (ADR-0019).

.DESCRIPTION
  Creates (or reuses) in Microsoft Entra ID and Azure:
    1. an app registration and service principal: cix-dashboard-deploy-<Environment> or cix-agents-deploy-<Environment>;
    2. a federated credential that trusts GitHub Actions tokens only for pushes to main of this repository
       (GitHub's immutable subject repo:<owner>@<owner id>/<repo>@<repo id>:ref:refs/heads/main, which a renamed or
       recreated repository can't match): pull requests, other branches and forks can't use it. It replaces, after
       asking, the name-only trust (repo:<owner>/<repo>:ref:refs/heads/main) an earlier version of this script made.
       For a protected environment it trusts only jobs in the GitHub environment of the same name instead
       (repo:<owner>@<owner id>/<repo>@<repo id>:environment:<Environment>), which waits for the owner's approval;
    3. its roles, and no others:
       - dashboard: a custom role, "CIX dashboard deployer (<Environment>)", that may only read a Static Web App and its
         deployment token, assigned on this environment's Static Web App alone;
       - agents: AcrPush on the environment's registry, and a custom role, "CIX agents deployer (<Environment>)", that
         may read and update container apps and jobs, assigned only on the registry (to find it) and, once
         agents.deploy is apps, on the engine and the three jobs. It can't create resources (it holds no right on
         the resource group) or role assignments, move an identity or an app to another environment, or reach Key
         Vault, storage or the database directly; the live engine gets a resource group
         of its own (docs/research/azure-hosting.md). With agents.deploy platform, run this again after deploying apps.
  Then prints the repository variables CI needs (IDs, not secrets), and sets them with the GitHub CLI if you pass
  -SetGitHubVariables and gh is installed and signed in. For a protected environment those are LIVE_*, and
  -SetGitHubVariables also gives the GitHub environment its rules: you as its required reviewer, main only.

  Each change is shown and confirmed first. Running it again changes nothing that is already in place.
  Needs rights to create app registrations and role assignments (Owner on the subscription is enough).

.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id>
.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id> -SetGitHubVariables
.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment dev -SubscriptionId <id> -For agents   # after provision.ps1 with agents.deploy platform
.EXAMPLE
  .\tools\azure\setup-ci-deploy.ps1 -Environment prod -For agents -SetGitHubVariables   # the live deploy (ADR-0019)
.PARAMETER RepositoryIds
  <owner id>/<repository id>, GitHub's numeric IDs for the trust. Without it they are read with the GitHub CLI (gh).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]{2,12}$')][string]$Environment,
  [ValidatePattern('^[0-9a-fA-F-]{36}$')][string]$SubscriptionId,
  [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
  [ValidatePattern('^\d+/\d+$')][string]$RepositoryIds,
  [ValidateSet('dashboard', 'agents')][string]$For = 'dashboard',
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
# GitHub's immutable subject: the only one its tokens carry for repositories created, renamed or transferred since 15 July 2026, or that opted in
# (the repository's OIDC settings). The IDs never change, so a repository renamed or recreated under this name can't match.
if (-not $RepositoryIds) {
  $ids = @()
  if (Get-Command gh -ErrorAction SilentlyContinue) {
    # Two plain queries: quotes inside native arguments don't survive Windows PowerShell 5.1.
    foreach ($query in '.owner.id', '.id') {
      $value = Invoke-CixNative { & gh api "repos/$Repository" --jq $query 2>$null }
      if ($LASTEXITCODE -eq 0 -and "$value".Trim() -match '^\d+$') { $ids += "$value".Trim() }
    }
  }
  if ($ids.Count -ne 2) { throw "Couldn't read the IDs of $Repository with the GitHub CLI: sign in (gh auth login), or pass -RepositoryIds <owner id>/<repository id>." }
  $RepositoryIds = $ids -join '/'
}
$repoOwner, $repoName = $Repository.Split('/')
$ownerId, $repoId = $RepositoryIds.Split('/')

Write-CixStep "Environment '$Environment'"
$settings = Read-CixEnvironment -Name $Environment
if ($For -eq 'dashboard' -and $settings.protected) { throw "$Environment is protected (live money): it has no dashboard of its own." }
$live = $For -eq 'agents' -and $settings.protected
if ($live) {
  # Only jobs in the GitHub environment of this name, which the owner approves each time (ADR-0019); no name-only trust ever existed.
  $subject = "repo:$repoOwner@$ownerId/$repoName@${repoId}:environment:$Environment"
  $legacySubject = $subject
}
else {
  $subject = "repo:$repoOwner@$ownerId/$repoName@${repoId}:ref:refs/heads/main"
  $legacySubject = "repo:${Repository}:ref:refs/heads/main" # what an earlier version of this script trusted
}
$identityName = "cix-$For-deploy-$Environment"
$roleName = "CIX $For deployer ($Environment)"
if ($For -eq 'dashboard') {
  $appName = "stapp-$($settings.namePrefix)-$($settings.environmentName)"
  $roleActions = @('Microsoft.Web/staticSites/listSecrets/action', 'Microsoft.Web/staticSites/read')
  $roleDescription = 'Reads one CIX Static Web App and its deployment token, to publish the dashboard from CI.'
  $limit = 'read this Static Web App and its deployment token'
  Write-CixInfo "Static Web App:  $appName in $($settings.resourceGroup)"
}
else {
  # Reading and updating the engine and the jobs (Get-CixAgentsDeployerActions): the update carries no environment or
  # identity, and needs no right on those. Each right applies only where the role is assigned: on those resources and the
  # registry, never on the group.
  $roleActions = @(Get-CixAgentsDeployerActions)
  $engine = if ($live) { 'live engine' } else { 'paper engine' }
  $roleDescription = "Reads and updates the CIX $engine and agent jobs it is assigned on, to deploy new images from CI."
  $limit = "push images and update the $engine and the jobs"
  if ($settings.agents.deploy -eq 'none') { throw "agents.deploy is none in $($settings.path): run provision.ps1 with agents.deploy platform first (it creates the registry)." }
  if ($live -and $settings.agents.deploy -ne 'apps') { throw "agents.deploy is $($settings.agents.deploy) in $($settings.path): deploy the live engine with provision.ps1 first (docs/live-trading.md)." }
  Write-CixInfo "Resource group:  $($settings.resourceGroup) (the $engine and the agent jobs)"
}
Write-CixInfo "Identity:        $identityName, trusted only for $subject"

Write-CixStep 'Signing in'
$account = Connect-CixAzure -Settings $settings -SubscriptionId $SubscriptionId
$groupId = "/subscriptions/$($account.id)/resourceGroups/$($settings.resourceGroup)"
if ($For -eq 'dashboard') {
  $site = Invoke-CixAz @('staticwebapp', 'show', '--name', $appName, '--resource-group', $settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson
  if (-not $site) { throw "Static Web App $appName not found in $($settings.resourceGroup); run provision.ps1 first." }
  $assignments = @(@{ Role = $roleName; Scope = $site.id; Label = $appName })
}
else {
  $registry = Find-CixAgentsResource -Settings $settings -Kind acr
  if (-not $registry) { throw "No container registry for $Environment in $($settings.resourceGroup); run provision.ps1 with agents.deploy platform first." }
  $registryId = "$(Invoke-CixAz @('acr', 'show', '--name', $registry, '--query', 'id', '-o', 'tsv', '--only-show-errors'))".Trim()
  $assignments = @(@{ Role = 'AcrPush'; Scope = $registryId; Label = $registry }, @{ Role = $roleName; Scope = $registryId; Label = $registry })
  if ($settings.agents.deploy -eq 'apps') {
    # Each must exist (provision.ps1 creates them).
    $targets = @(Get-CixAgentsDeployTargets -Settings $settings)
    $existing = @(Invoke-CixAz @('resource', 'list', '--resource-group', $settings.resourceGroup, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
        ForEach-Object { "$($_.id)" })
    foreach ($target in $targets) {
      $id = "$groupId/providers/$target"
      if ($existing -notcontains $id) { throw "$($target.Split('/')[-1]) isn't in $($settings.resourceGroup); run provision.ps1 with agents.deploy apps first." }
      $assignments += @{ Role = $roleName; Scope = $id; Label = $target.Split('/')[-1] }
    }
  }
}

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
      if ($held.Count -gt 0) { throw "The service principal of $identityName has $relation (API permissions or directory roles/groups). Remove them first: it may only $limit." }
    }
    # ...and no Azure role but ours, on our scopes.
    $expected = @($assignments | ForEach-Object { "$($_.Role)|$($_.Scope)" })
    $strays = @(Invoke-CixAz @('role', 'assignment', 'list', '--assignee', $sp.id, '--all', '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
        Where-Object { $expected -notcontains "$($_.roleDefinitionName)|$($_.scope)" })
    if ($strays.Count -gt 0) {
      throw "$identityName also holds $(($strays | ForEach-Object { "$($_.roleDefinitionName) on $($_.scope)" }) -join '; '). Remove those role assignments first: it may only $limit."
    }
  }

  # It may trust GitHub Actions on main of this repository (for live, its GitHub environment), and nothing else.
  $credentials = @(Invoke-CixAz @('ad', 'app', 'federated-credential', 'list', '--id', $app.appId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
  $others = @($credentials | Where-Object {
      ($_.subject -cne $subject -and $_.subject -cne $legacySubject) -or $_.issuer -ne 'https://token.actions.githubusercontent.com' -or
      (@(& $field $_ 'audiences') -join ',') -ne 'api://AzureADTokenExchange' })
  if ($others.Count -gt 0) {
    throw "$identityName also trusts $(($others | ForEach-Object { "$($_.subject) (issuer $($_.issuer))" }) -join ', '). Remove those in the Azure portal (App registrations, Certificates & secrets) first: only main of $Repository may deploy."
  }
}

# An existing role with this name must grant only expected actions, on this resource group only. One from an earlier
# version of this script may lack some; they are added below.
$missingActions = @()
$role = @(Invoke-CixAz @('role', 'definition', 'list', '--name', $roleName, '--custom-role-only', 'true', '--scope', $groupId, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson)
if ($role.Count -gt 0) {
  $p = @(& $field $role[0] 'permissions')
  $actions = @($p | ForEach-Object { & $field $_ 'actions' } | Where-Object { $_ }) | Sort-Object
  $extra = @($p | ForEach-Object { @(& $field $_ 'notActions') + @(& $field $_ 'dataActions') + @(& $field $_ 'notDataActions') } | Where-Object { $_ })
  $unexpected = @($actions | Where-Object { $roleActions -cnotcontains $_ })
  if ($unexpected.Count -gt 0 -or $extra.Count -gt 0 -or ((& $field $role[0] 'assignableScopes') -join ',') -ne $groupId) {
    throw "The existing role '$roleName' grants other rights than it should (it may only $limit). Delete it first (az role definition delete --name `"$roleName`")."
  }
  $missingActions = @($roleActions | Where-Object { $actions -cnotcontains $_ })
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

Write-CixStep "Roles: $limit, nothing else"
if ($role.Count -eq 0) {
  Confirm-Step "Create the custom role '$roleName' ($($roleActions -join ', '), assignable within $($settings.resourceGroup) only)?"
  $file = Write-CixTempJson ([ordered]@{
      Name             = $roleName
      Description      = $roleDescription
      Actions          = @($roleActions)
      NotActions       = @()
      AssignableScopes = @($groupId)
    })
  try { Invoke-CixAz @('role', 'definition', 'create', '--role-definition', "@$file", '--only-show-errors', '-o', 'none') | Out-Null }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo 'Created.'
}
elseif ($missingActions.Count -gt 0) {
  Confirm-Step "Add $($missingActions -join ', ') to the role '$roleName'?"
  $file = Write-CixTempJson ([ordered]@{
      Name             = $roleName
      Description      = $roleDescription
      Actions          = @($roleActions)
      NotActions       = @()
      AssignableScopes = @($groupId)
    })
  try { Invoke-CixAz @('role', 'definition', 'update', '--role-definition', "@$file", '--only-show-errors', '-o', 'none') | Out-Null }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo "Added $($missingActions -join ', ')."
}
else { Write-CixInfo 'Exists, with exactly the expected actions.' }

foreach ($assignment in $assignments) {
  $assigned = @(Invoke-CixAz @('role', 'assignment', 'list', '--assignee', $sp.id, '--scope', $assignment.Scope, '--only-show-errors', '-o', 'json') | ConvertFrom-CixJson |
      Where-Object { $_.roleDefinitionName -eq $assignment.Role })
  if ($assigned.Count -gt 0) { Write-CixInfo "Already assigned $($assignment.Role) on $($assignment.Label)."; continue }
  Confirm-Step "Assign '$($assignment.Role)' to $identityName on $($assignment.Label) only?"
  # A new role definition can take a minute to become assignable.
  for ($attempt = 1; ; $attempt++) {
    try {
      Invoke-CixAz @('role', 'assignment', 'create', '--assignee-object-id', $sp.id, '--assignee-principal-type', 'ServicePrincipal',
        '--role', $assignment.Role, '--scope', $assignment.Scope, '--only-show-errors', '-o', 'none') | Out-Null
      break
    }
    catch {
      if ($attempt -ge 6) { throw }
      Write-CixInfo 'Waiting for the new role to become assignable...'
      Start-Sleep -Seconds 20
    }
  }
  Write-CixInfo "Assigned $($assignment.Role) on $($assignment.Label)."
}

$trusted = if ($live) { "jobs of $Repository in its GitHub environment $Environment (owner-approved, main only)" } else { "GitHub Actions on main of $Repository" }
Write-CixStep $(if ($live) { "Federated credential (GitHub environment $Environment only)" } else { 'Federated credential (GitHub Actions, main only)' })
if (@($credentials | Where-Object { $_.subject -ceq $subject }).Count -eq 1) { Write-CixInfo "Already trusts $subject." }
else {
  Confirm-Step "Let $trusted sign in as ${identityName}?"
  $file = Write-CixTempJson ([ordered]@{
      name        = $(if ($live) { "github-environment-$Environment" } else { 'github-main-immutable' })
      issuer      = 'https://token.actions.githubusercontent.com'
      subject     = $subject
      audiences   = @('api://AzureADTokenExchange')
      description = $(if ($live) { "CI deploys CIX to $Environment after the owner approves (tools/azure/setup-ci-deploy.ps1, ADR-0019)." } else { "CI deploys CIX ($For) from main (tools/azure/setup-ci-deploy.ps1)." })
    })
  try { Invoke-CixAz @('ad', 'app', 'federated-credential', 'create', '--id', $app.appId, '--parameters', "@$file", '--only-show-errors', '-o', 'none') | Out-Null }
  finally { Remove-Item $file -ErrorAction SilentlyContinue }
  Write-CixInfo "Trusts $subject."
}
# The name-only trust an earlier version made: GitHub doesn't send that subject once the repository uses immutable ones.
foreach ($legacy in @($credentials | Where-Object { $_.subject -ceq $legacySubject -and -not $live })) {
  Write-CixInfo "CI can sign in afterwards only if GitHub sends $subject (automatic for repositories created since 15 July 2026; otherwise opt in under the repository's OIDC settings)."
  Confirm-Step "Remove the name-only trust $legacySubject from $identityName (a repository renamed or recreated under this name would match it)?"
  Invoke-CixAz @('ad', 'app', 'federated-credential', 'delete', '--id', $app.appId, '--federated-credential-id', "$(& $field $legacy 'id')", '--only-show-errors') | Out-Null
  Write-CixInfo "Removed the trust for $legacySubject."
}

Write-CixStep 'GitHub repository variables (IDs, not secrets)'
if ($live) {
  # Its own names: the paper deploy's AGENTS_* and AZURE_* variables stay as they are. The job's GitHub environment is fixed in ci.yml.
  $variables = [ordered]@{ LIVE_AZURE_CLIENT_ID = $app.appId; LIVE_REGISTRY = $registry; LIVE_TENANT_ID = $account.tenantId; LIVE_SUBSCRIPTION_ID = $account.id }
}
else {
  $variables = if ($For -eq 'dashboard') { [ordered]@{ AZURE_CLIENT_ID = $app.appId } } else { [ordered]@{ AGENTS_AZURE_CLIENT_ID = $app.appId; AGENTS_REGISTRY = $registry } }
  $variables['AZURE_TENANT_ID'] = $account.tenantId
  $variables['AZURE_SUBSCRIPTION_ID'] = $account.id
  $variables[$(if ($For -eq 'dashboard') { 'DASHBOARD_ENVIRONMENT' } else { 'AGENTS_ENVIRONMENT' })] = $Environment
}
if ($SetGitHubVariables) {
  Assert-CixCommand -Name 'gh' -Hint 'Install the GitHub CLI (winget install GitHub.cli) and run gh auth login, or set the variables by hand.'
  if ($live) {
    # Before the variables, which switch the live deploy on: the environment holds every job until you approve it, and only
    # from main. Admins can't skip the review, and you may approve runs you started yourself.
    Write-CixStep "GitHub environment $Environment"
    Confirm-Step "Make you ($repoOwner) the required reviewer of the GitHub environment $Environment, for main only?"
    $file = Write-CixTempJson ([ordered]@{
        wait_timer               = 0
        prevent_self_review      = $false
        reviewers                = @([ordered]@{ type = 'User'; id = [long]$ownerId })
        can_admins_bypass        = $false
        deployment_branch_policy = [ordered]@{ protected_branches = $false; custom_branch_policies = $true }
      })
    try { Invoke-CixNative { & gh api --method PUT "repos/$Repository/environments/$Environment" --input $file } | Out-Null }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "Couldn't set the rules of the GitHub environment $Environment (the repository must belong to you, a user)." }
    $listed = Invoke-CixNative { & gh api "repos/$Repository/environments/$Environment/deployment-branch-policies" --jq .branch_policies 2>$null }
    if ($LASTEXITCODE -ne 0) { throw "Couldn't read the branches the GitHub environment $Environment allows." }
    $branches = @("$listed" | ConvertFrom-CixJson | ForEach-Object { "$($_.type):$($_.name)" })
    $others = @($branches | Where-Object { $_ -cne 'branch:main' })
    if ($others.Count -gt 0) { throw "The GitHub environment $Environment also allows $($others -join ', '). Remove those under Settings, Environments, $Environment first: only main may deploy." }
    if ($branches -cnotcontains 'branch:main') {
      Invoke-CixNative { & gh api --method POST "repos/$Repository/environments/$Environment/deployment-branch-policies" -f name=main -f type=branch } | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "Couldn't limit the GitHub environment $Environment to main." }
    }
    Write-CixInfo "The GitHub environment $Environment waits for your approval, and only runs from main."
  }
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
  if ($live) {
    Write-CixWarn "Before you set them: in GitHub, Settings, Environments, create $Environment with you as its required reviewer, deployment branches main only, and leave 'Prevent self-review' off (docs/live-trading.md, Deploys from main)."
  }
}
if ($For -eq 'agents' -and $settings.agents.deploy -ne 'apps') {
  Write-CixInfo 'CI can push images now. After provision.ps1 deploys agents.deploy apps, run this again.'
}
if ($live) {
  Write-CixInfo "From now on each change on main that reaches the paper engine waits in CI for your approval (Review deployments, $Environment), then deploys to the live engine, which keeps its mode and strategy."
}
else { Write-CixInfo "Then run CI on main by hand once (Actions, CI, Run workflow) to check it deploys the $For." }
