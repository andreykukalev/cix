<#
.SYNOPSIS
  Creates the secret files Docker Compose mounts into the containers: secrets\operator_key and secrets\claude_token.

.DESCRIPTION
  Run once before the first `docker compose up -d --build`, and again with -Claude when the Claude token is renewed.
  - operator_key: a random key that unlocks owner tools (pause, resume, journal). Generated if missing.
  - claude_token: a long-lived token from `claude setup-token` (recommended, uses your Claude subscription) or an
    Anthropic API key (sk-ant-api..., billed per use). Typed in hidden; never printed.
  - .env: CLAUDE_TOKEN_EXPIRES (not a secret) so the trader warns 14 days before a setup token expires.
  secrets\ is git-ignored and readable only by you; .env is git-ignored and holds no secret. Nothing here is sent anywhere.

.EXAMPLE
  .\tools\setup-secrets.ps1            # first time
.EXAMPLE
  .\tools\setup-secrets.ps1 -Claude    # after `claude setup-token` gave you a new token
#>
param(
    [switch]$Claude,
    [switch]$RotateOperatorKey
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$secrets = Join-Path $repo 'secrets'
New-Item -ItemType Directory -Force -Path $secrets | Out-Null
# Grant by SID: works for local, Microsoft, Entra and domain accounts alike.
$me = "*$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
function Restrict([string]$path, [string]$rights) {
    icacls $path /inheritance:r /grant:r "${me}:$rights" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $path (icacls exit $LASTEXITCODE)." }
}
# Restrict the folder first, so files created in it never inherit wider access.
Restrict $secrets '(OI)(CI)F'
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'logs\trader') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'logs\analyst') | Out-Null

function Write-Secret([string]$name, [string]$value) {
    $path = Join-Path $secrets $name
    [IO.File]::WriteAllText($path, $value, (New-Object Text.UTF8Encoding($false)))
    # Only the current user may read it (Docker Desktop reads it as you).
    Restrict $path '(R,W)'
}

function Set-EnvValue([string]$key, [string]$value) {
    $envFile = Join-Path $repo '.env'
    $lines = @()
    if (Test-Path $envFile) { $lines = @(Get-Content $envFile | Where-Object { $_ -notmatch "^$key=" }) }
    $lines += "$key=$value"
    Set-Content -Path $envFile -Value $lines -Encoding ascii
}

# Owner key
$operatorPath = Join-Path $secrets 'operator_key'
if ($RotateOperatorKey -or -not (Test-Path $operatorPath)) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    Write-Secret 'operator_key' ([Convert]::ToBase64String($bytes))
    Write-Host "Owner key written to secrets\operator_key (not shown). Use it for owner sessions; agents never get it."
}

# Claude credentials
$claudePath = Join-Path $secrets 'claude_token'
if ($Claude -or -not (Test-Path $claudePath)) {
    Write-Host ""
    Write-Host "Claude credentials for the trader container:"
    Write-Host "  Recommended: run 'claude setup-token' in another terminal and paste the token it prints (valid one year)."
    Write-Host "  Alternative: an Anthropic API key from console.anthropic.com (sk-ant-api...)."
    $secure = Read-Host "Paste the token or key (hidden)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $plain = $plain.Trim()
    if ($plain -notmatch '^sk-ant-[A-Za-z0-9_-]+$') {
        throw "That doesn't look like a Claude setup token or API key (expected sk-ant-...). Nothing was saved."
    }
    $isApiKey = $plain -like 'sk-ant-api*'
    $expires = ''
    if (-not $isApiKey) {
        $default = (Get-Date).AddDays(365).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        $answer = Read-Host "Token expiry date [$default]"
        $expires = if ([string]::IsNullOrWhiteSpace($answer)) { $default } else { $answer.Trim() }
        $parsed = [DateTime]::MinValue
        if (-not [DateTime]::TryParseExact($expires, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)) {
            throw "Expiry must be a date like $default. Nothing was saved."
        }
    }

    Write-Secret 'claude_token' $plain
    $plain = $null
    Set-EnvValue 'CLAUDE_TOKEN_EXPIRES' $expires
    if ($isApiKey) { Write-Host "API key saved to secrets\claude_token." }
    else { Write-Host "Setup token saved to secrets\claude_token; expiry $expires recorded in .env." }
}

Write-Host ""
Write-Host "Done. Start everything with: docker compose up -d --build"
Write-Host "After renewing the Claude token: .\tools\setup-secrets.ps1 -Claude; docker compose restart trader"
