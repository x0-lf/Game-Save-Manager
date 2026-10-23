<#
.SYNOPSIS
Publishes GameSaves.App self-contained for win-x64 and builds the Windows installer.

.DESCRIPTION
RELEASE-001. Produces, under artifacts/release (git-ignored):

  publish\win-x64\                      the self-contained single-folder publish of GameSaves.App
  GameSaveManager-Setup-<version>.exe   the per-user Inno Setup installer
  GameSaveManager-Setup-<version>.exe.sha256
                                        its SHA-256 digest ("<hex>  <name>"), which the in-app
                                        update check verifies a download against (RELEASE-002)

Only GameSaves.App is published: never the CLI, Reviewer, tests or capture
harnesses. Nothing under bin\ or obj\ is read or written (the isolated
artifacts path is used), the application is never started, and no user data
under %LOCALAPPDATA%\GameSave is touched.

The version is the single <Version> element of Manager/GameSaves.App/GameSaves.App.csproj.

Prerequisites: the .NET 10 SDK; Inno Setup 6.3 or newer for the installer
(winget install JRSoftware.InnoSetup); signtool from the Windows SDK only when signing.

.PARAMETER CertificatePath
PFX file used to sign GameSaves.App.exe and the installer.
Default: $env:GSM_SIGNING_CERTIFICATE. When neither is set the package is built
unsigned and the script says so.

.PARAMETER CertificatePassword
Password of the PFX. Default: $env:GSM_SIGNING_PASSWORD.

.PARAMETER TimestampUrl
RFC 3161 timestamp server used when signing.

.PARAMETER GoogleDesktopClientId
The project's public Google OAuth desktop client id, baked into the package so
users connect Google Drive without setting GAMESAVES_GOOGLE_CLIENT_ID (OBS-027).
Default: $env:GSM_GOOGLE_DESKTOP_CLIENT_ID. Empty builds a package without a
Google client, which then behaves like a development build. A client id is
public by Google's definition; the client secret is never taken by this script.

.PARAMETER OutputRoot
Where the publish folder and the installer land. Default: artifacts\release under the repository root.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File build/publish-release.ps1
#>
[CmdletBinding()]
param(
    [string]$CertificatePath = $env:GSM_SIGNING_CERTIFICATE,
    [string]$CertificatePassword = $env:GSM_SIGNING_PASSWORD,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$GoogleDesktopClientId = $env:GSM_GOOGLE_DESKTOP_CLIENT_ID,
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = 'Manager/GameSaves.App/GameSaves.App.csproj'
$projectPath = Join-Path $repoRoot $project
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\release' }
$publishDir = Join-Path $OutputRoot 'publish\win-x64'
$issPath = Join-Path $PSScriptRoot 'GameSaveManager.iss'

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path (Join-Path $kits '*\x64\signtool.exe') -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw 'A signing certificate was supplied but signtool.exe was not found. Install the Windows SDK or put signtool on PATH.'
}

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$signtool = $null
$signing = -not [string]::IsNullOrWhiteSpace($CertificatePath)
if ($signing) {
    if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Signing certificate not found: $CertificatePath" }
    $signtool = Find-SignTool
}

function Invoke-Sign([string]$File) {
    & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $CertificatePath /p $CertificatePassword $File
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $File with exit code $LASTEXITCODE." }
}

# 1. Version: the single source of truth, shared with the executable's file
#    properties, the installer and the Settings diagnostics.
$version = (Select-Xml -Path $projectPath -XPath '/Project/PropertyGroup/Version').Node.InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "GameSaves.App.csproj <Version> must be MAJOR.MINOR.PATCH (Inno Setup's VersionInfoVersion is numeric); found '$version'."
}
Write-Host "Game Save Manager $version"

# 1b. Google Drive client (OBS-027): the public desktop client id baked into
#     the package, so users connect without GAMESAVES_GOOGLE_CLIENT_ID. The
#     client secret belongs to a developer's environment and never enters a
#     build; a developer's own GAMESAVES_GOOGLE_CLIENT_ID still overrides this.
if ($GoogleDesktopClientId -and $GoogleDesktopClientId -notmatch '^[A-Za-z0-9._-]+\.apps\.googleusercontent\.com$') {
    throw "GoogleDesktopClientId must be a Google OAuth desktop client id ending in .apps.googleusercontent.com; found '$GoogleDesktopClientId'."
}
if ($GoogleDesktopClientId) {
    Write-Host "Google Drive client: $GoogleDesktopClientId (built in)"
}
else {
    Write-Warning 'Google Drive client: none (GSM_GOOGLE_DESKTOP_CLIENT_ID is empty). Users of this package must set GAMESAVES_GOOGLE_CLIENT_ID before connecting Google Drive.'
}

# 2. Publish: self-contained, single folder, win-x64, Release, through the
#    isolated artifacts path so a running app that locks bin\ cannot interfere.
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
Push-Location $repoRoot
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path Manager/.artifacts -o $publishDir -v q --nologo "-p:GoogleDesktopClientId=$GoogleDesktopClientId"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$appExe = Join-Path $publishDir 'GameSaves.App.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw "Publish did not produce $appExe." }
$strays = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Name -match '^GameSaves\.(Tests|UiCapture|UiMaterialCapture|Reviewer)\b|^GameSaves\.(exe|dll)$|^xunit|^testhost' }
if ($strays) { throw "The publish folder contains files that must never ship: $($strays.Name -join ', ')" }

$publishBytes = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ('Publish: {0} ({1} MB)' -f $publishDir, [math]::Round($publishBytes / 1MB))

# 2b. Seed database: the sanitized catalog exported by `GameSaves seed-export`
#     and committed under build\seed. It ships beside the program; the app
#     copies it to the user's data folder on its first start only when no
#     database exists there yet, and never replaces an existing one.
$seed = Join-Path $PSScriptRoot 'seed\gamesave.db'
if (Test-Path -LiteralPath $seed) {
    New-Item -ItemType Directory -Path (Join-Path $publishDir 'seed') -Force | Out-Null
    Copy-Item -LiteralPath $seed -Destination (Join-Path $publishDir 'seed\gamesave.db')
    Write-Host ('Seed database: {0} ({1} MB)' -f $seed, [math]::Round((Get-Item -LiteralPath $seed).Length / 1MB, 1))
}
else {
    Write-Warning 'No seed database (build\seed\gamesave.db is absent): a first start gets only the bundled curated mappings.'
}

# 3. Signing, only with a supplied certificate. Never silent about the alternative.
if ($signing) {
    Invoke-Sign $appExe
}
else {
    Write-Warning 'Package is unsigned: no certificate supplied (-CertificatePath or GSM_SIGNING_CERTIFICATE). Windows SmartScreen warns on first run.'
}

# 4. Installer.
$iscc = Find-Iscc
if (-not $iscc) {
    Write-Warning "Inno Setup 6 was not found, so no installer was built. The publish folder above is complete. Install it with 'winget install JRSoftware.InnoSetup' and run this script again."
    exit 2
}
& $iscc /Qp "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$OutputRoot" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$installer = Join-Path $OutputRoot "GameSaveManager-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw "ISCC reported success but $installer is missing." }
if ($signing) { Invoke-Sign $installer }

# 5. Digest, computed after signing because signing changes the bytes: the
#    file the in-app update check (RELEASE-002) verifies a download against.
#    Lowercase hex, two spaces, file name, as sha256sum writes it; UTF-8
#    without BOM, LF. Published beside the installer under the same name
#    plus ".sha256".
$digestPath = "$installer.sha256"
$digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($digestPath, "$digest  $(Split-Path -Leaf $installer)`n", (New-Object System.Text.UTF8Encoding($false)))

$signedText = if ($signing) { 'signed' } else { 'unsigned' }
Write-Host ('Installer: {0} ({1} MB, {2})' -f $installer, [math]::Round((Get-Item -LiteralPath $installer).Length / 1MB), $signedText)
Write-Host ('Digest:    {0} ({1})' -f $digestPath, $digest)
