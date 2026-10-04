<#
Registers The Plane Web in a company Entra ID directory (e.g. CapTech) so its employees can sign in,
then stores the values in GitHub so the next deploy turns on Microsoft sign-in.

Needs (Windows): Azure CLI   winget install Microsoft.AzureCLI
                 GitHub CLI  winget install GitHub.cli
Permission to create app registrations in the directory (or have IT run this).

Usage (PowerShell):
  .\scripts\setup-entra.ps1 -AppUrl https://<app url> -Tenant captech.com
If scripts are blocked:  powershell -ExecutionPolicy Bypass -File .\scripts\setup-entra.ps1 -AppUrl ... -Tenant ...
#>
param(
    [Parameter(Mandatory)] [string] $AppUrl,
    [string] $Tenant = "",
    [string] $Repo = "craigmoliver/the-plane-web",
    [string] $Name = "The Plane Web"
)
$ErrorActionPreference = "Stop"

function Run([string] $exe, [string[]] $argList) {
    $out = & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "$exe $($argList -join ' ') failed (exit $LASTEXITCODE)" }
    return $out
}

$AppUrl = $AppUrl.TrimEnd('/')
if (-not $AppUrl.StartsWith("https://")) { throw "The app URL must start with https://" }
foreach ($c in "az", "gh") {
    if (-not (Get-Command $c -ErrorAction SilentlyContinue)) { throw "Missing '$c'. Install it (see the top of this script) and reopen PowerShell." }
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Run gh @("auth", "login") | Out-Null }

Write-Host "== Signing in to Azure ==" -ForegroundColor Cyan
if ($Tenant) { Run az @("login", "--tenant", $Tenant, "-o", "none") | Out-Null }
else { Run az @("login", "-o", "none") | Out-Null }
$TenantId = Run az @("account", "show", "--query", "tenantId", "-o", "tsv")
$Me = Run az @("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv")
$Upn = Run az @("ad", "signed-in-user", "show", "--query", "userPrincipalName", "-o", "tsv")
Write-Host "Directory: $TenantId   You: $Upn"

Write-Host "== App registration ==" -ForegroundColor Cyan
$Redirect = "$AppUrl/signin-oidc"
$AppId = Run az @("ad", "app", "list", "--display-name", $Name, "--query", "[0].appId", "-o", "tsv")
if (-not $AppId) {
    $AppId = Run az @("ad", "app", "create", "--display-name", $Name, "--sign-in-audience", "AzureADMyOrg",
        "--web-redirect-uris", $Redirect, "--query", "appId", "-o", "tsv")
    Write-Host "Created $AppId"
} else {
    Run az @("ad", "app", "update", "--id", $AppId, "--web-redirect-uris", $Redirect) | Out-Null
    Write-Host "Reusing $AppId (redirect URI updated)"
}
# Enterprise application (lets IT require assignment / restrict to a group later)
& az ad sp show --id $AppId -o none 2>$null
if ($LASTEXITCODE -ne 0) { Run az @("ad", "sp", "create", "--id", $AppId, "-o", "none") | Out-Null }

Write-Host "== Client secret (valid 1 year) ==" -ForegroundColor Cyan
$Secret = Run az @("ad", "app", "credential", "reset", "--id", $AppId, "--display-name", "planeweb",
    "--years", "1", "--append", "--query", "password", "-o", "tsv")
$Expires = (Get-Date).AddYears(1).ToString("yyyy-MM-dd")

Write-Host "== Saving to GitHub ($Repo) ==" -ForegroundColor Cyan
Run gh @("variable", "set", "ENTRA_TENANT_ID", "-R", $Repo, "-b", $TenantId) | Out-Null
Run gh @("variable", "set", "ENTRA_CLIENT_ID", "-R", $Repo, "-b", $AppId) | Out-Null
Run gh @("variable", "set", "ENTRA_ADMIN_OBJECT_IDS", "-R", $Repo, "-b", $Me) | Out-Null
# Piped on stdin so the secret never appears on a command line.
$Secret | & gh secret set ENTRA_CLIENT_SECRET -R $Repo --env production
if ($LASTEXITCODE -ne 0) { throw "Saving ENTRA_CLIENT_SECRET failed" }
Remove-Variable Secret

Write-Host "== Triggering a deploy ==" -ForegroundColor Cyan
Run gh @("workflow", "run", "deploy.yml", "-R", $Repo, "--ref", "main") | Out-Null

Write-Host @"

Done.
  Tenant:   $TenantId
  Client:   $AppId
  Admin:    you ($Upn)
  Redirect: $Redirect
  Secret expires about $Expires; rerun this script before then.

The deploy takes a few minutes:  gh run watch -R $Repo
Then open $AppUrl and choose "Sign in with your work account".
To limit access to a group: Entra ID > Enterprise applications > $Name >
  Properties > Assignment required = Yes, then Users and groups > add the group.
"@ -ForegroundColor Green
