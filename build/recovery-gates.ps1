<#
.SYNOPSIS
Installs, starts, upgrades, refuses and uninstalls the release package on a clean CI runner.

.DESCRIPTION
OBS-028. The recovery gates of .github/workflows/release.yml, run against the
installer the build job produced, on a fresh runner of the same image:

  gate 1  install    silent per-user install exits 0, the program is at
                     %LOCALAPPDATA%\Programs\Game Save Manager with the
                     packaged version, and nothing appears under
                     %LOCALAPPDATA%\GameSave.
  gate 2  start      the installed program is self-contained (hostfxr,
                     hostpolicy and coreclr beside it, includedFrameworks in
                     its runtimeconfig) and starts headless with PATH reduced
                     to System32 and DOTNET_ROOT pointing at an empty folder,
                     where a framework-dependent program cannot find a
                     runtime: --run-job <unknown id> returns 2 (refused),
                     which means it composed every service, created and
                     migrated its database, and declined an unknown job.
  gate 3  upgrade    the same installer again over the installed copy exits
                     0, the data folder's listing is unchanged by the
                     upgrade, and the upgraded program starts the same way.
  gate 4  refusal    a future row in schema_migrations makes the next
                     headless start return 4 (failed) with every byte under
                     the data folder unchanged: an older program never
                     writes a newer database (RELEASE-003).
  gate 5  uninstall  the silent uninstall exits 0, the install folder is
                     gone, and the data folder is exactly as gate 4 left it.

The gates write a future migration row into %LOCALAPPDATA%\GameSave, so the
script refuses to run unless GITHUB_ACTIONS is "true" and neither the data
folder nor the install folder exists yet. Nothing under the data folder is
ever deleted by this script. Install and uninstall logs land in
$env:RUNNER_TEMP\recovery-gates.

.PARAMETER Installer
GameSaveManager-Setup-<version>.exe from build/publish-release.ps1.

.PARAMETER Version
The version the installer was built from (the project <Version>).

.EXAMPLE
./build/recovery-gates.ps1 -Installer package/GameSaveManager-Setup-0.1.0.exe -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Installer,
    [Parameter(Mandatory = $true)] [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDir = Join-Path $env:LOCALAPPDATA 'GameSave'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\Game Save Manager'
$appExe = Join-Path $appDir 'GameSaves.App.exe'
$Refused = 2   # ScheduledBackupOutcome.Refused: started, composed, declined an unknown job
$Failed = 4    # ScheduledBackupOutcome.Failed: composition failed, here the newer-schema refusal

if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Refusing to run: the recovery gates write a future migration row under %LOCALAPPDATA%\GameSave and run only on a GitHub Actions runner (GITHUB_ACTIONS=true). Nothing was changed.'
}
foreach ($dir in @($dataDir, $appDir)) {
    if (Test-Path -LiteralPath $dir) {
        throw "Refusing to run: $dir already exists and the gates need a machine that never ran Game Save Manager. Nothing was changed."
    }
}
if (-not (Test-Path -LiteralPath $Installer)) { throw "Installer not found: $Installer" }
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$logDir = Join-Path $env:RUNNER_TEMP 'recovery-gates'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$script:gate = 0

function Pass([string]$Message) {
    $line = 'gate {0} pass: {1}' -f $script:gate, $Message
    Write-Host $line
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- $line" }
}

function Fail([string]$Message) {
    throw ('gate {0} FAIL: {1}' -f $script:gate, $Message)
}

function Assert-Same($Expected, $Actual, [string]$What) {
    if ($Expected -ne $Actual) {
        Fail ("{0}`n  expected:`n{1}`n  actual:`n{2}" -f $What, $Expected, $Actual)
    }
}

# Runs a program to completion and returns its exit code; a hang is a failure, not a wait.
function Invoke-Program([string]$Name, [string]$File, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -NoNewWindow
    $null = $process.Handle   # cache it, or ExitCode can read as null after the exit
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        Fail "$Name did not exit within $TimeoutSeconds s and was killed."
    }
    return $process.ExitCode
}

# Every file under the data folder with its size and SHA-256, one line each,
# sorted: two listings are equal only if no byte under the folder changed.
function Get-DataListing {
    if (-not (Test-Path -LiteralPath $dataDir)) { return '(no data folder)' }
    $lines = Get-ChildItem -LiteralPath $dataDir -Recurse -File -Force |
        ForEach-Object {
            '{0}  {1}  {2}' -f $_.FullName.Substring($dataDir.Length + 1), $_.Length,
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        } |
        Sort-Object
    return ($lines -join "`n")
}

# A framework-dependent program given a DOTNET_ROOT without a runtime stops
# with "hostfxr.dll could not be found"; a self-contained one never looks there.
function Start-Headless([string]$Why) {
    $saved = @{ PATH = $env:PATH; DOTNET_ROOT = $env:DOTNET_ROOT; DOTNET_ROOT_X64 = $env:DOTNET_ROOT_X64 }
    $emptyRoot = Join-Path $logDir 'empty-dotnet-root'
    New-Item -ItemType Directory -Force -Path $emptyRoot | Out-Null
    try {
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
        $env:DOTNET_ROOT = $emptyRoot
        $env:DOTNET_ROOT_X64 = $emptyRoot
        return Invoke-Program "headless start ($Why)" $appExe @('--run-job', [guid]::NewGuid().ToString('D')) 180
    }
    finally {
        $env:PATH = $saved.PATH
        $env:DOTNET_ROOT = $saved.DOTNET_ROOT
        $env:DOTNET_ROOT_X64 = $saved.DOTNET_ROOT_X64
    }
}

$silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

# gate 1: install
$script:gate = 1
$code = Invoke-Program 'install' $Installer ($silent + "/LOG=$logDir\install.log") 300
Assert-Same 0 $code 'installer exit code'
if (-not (Test-Path -LiteralPath $appExe)) { Fail "$appExe was not installed" }
if (Test-Path -LiteralPath $dataDir) { Fail "the installer created $dataDir" }
$installedVersion = (Get-Item -LiteralPath $appExe).VersionInfo.ProductVersion
if (-not $installedVersion.StartsWith($Version)) { Fail "the installed program reports version '$installedVersion', expected $Version" }
Pass "installed $Version under $appDir; $dataDir does not exist"

# gate 2: self-contained headless start
$script:gate = 2
foreach ($file in @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $file))) { Fail "$file is missing beside the program: the package is not self-contained" }
}
$runtimeOptions = (Get-Content -LiteralPath (Join-Path $appDir 'GameSaves.App.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
if (-not ($runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks')) { Fail 'GameSaves.App.runtimeconfig.json has no includedFrameworks: the package is not self-contained' }
$code = Start-Headless 'fresh install'
Assert-Same $Refused $code 'headless --run-job <unknown id> exit code (2 = refused: the program composed its services and declined an unknown job)'
if (-not (Test-Path -LiteralPath (Join-Path $dataDir 'gamesave.db'))) { Fail 'the headless start did not create gamesave.db' }
$afterFirstStart = Get-DataListing
Pass "the self-contained program started with no .NET runtime reachable and created $dataDir"

# gate 3: upgrade in place
$script:gate = 3
$code = Invoke-Program 'upgrade' $Installer ($silent + "/LOG=$logDir\upgrade.log") 300
Assert-Same 0 $code 'upgrade (the same installer again) exit code'
Assert-Same $afterFirstStart (Get-DataListing) 'data folder listing after the upgrade (an upgrade changes nothing there)'
$code = Start-Headless 'after upgrade'
Assert-Same $Refused $code 'headless exit code after the upgrade'
Pass 'upgraded in place; the upgrade changed nothing under the data folder; the upgraded program starts'

# gate 4: an older program meets a newer database
$script:gate = 4
$database = Join-Path $dataDir 'gamesave.db'
$futureRow = Join-Path $logDir 'newer-database.py'
[IO.File]::WriteAllText($futureRow, @'
import sqlite3, sys
connection = sqlite3.connect(sys.argv[1])
connection.execute("INSERT INTO schema_migrations (id, name, applied_utc) VALUES (?, ?, ?)", (99, "V099__FromTheFuture", "2027-01-01 00:00:00"))
connection.commit()
connection.close()
'@)
& (Get-Command python -ErrorAction Stop).Source -I $futureRow $database
if ($LASTEXITCODE -ne 0) { Fail 'the future migration row could not be written' }
$newerDatabase = Get-DataListing
Assert-Same $false ($newerDatabase -eq $afterFirstStart) 'the database changed after the future row was written'
$code = Start-Headless 'newer database'
Assert-Same $Failed $code 'headless exit code against a newer database (4 = failed: the RELEASE-003 refusal)'
Assert-Same $newerDatabase (Get-DataListing) 'data folder listing after the refusal (the older program writes nothing)'
Pass 'the older program refused the newer database and changed no byte under the data folder'

# gate 5: uninstall
$script:gate = 5
$uninstaller = Join-Path $appDir 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { Fail "$uninstaller is missing" }
$code = Invoke-Program 'uninstall' $uninstaller ($silent + "/LOG=$logDir\uninstall.log") 300
Assert-Same 0 $code 'uninstaller exit code'
$deadline = (Get-Date).AddSeconds(60)   # the uninstaller's temp copy removes the folder after the first process exits
while ((Test-Path -LiteralPath $appDir) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Seconds 2 }
if (Test-Path -LiteralPath $appDir) {
    $leftovers = (Get-ChildItem -LiteralPath $appDir -Recurse -Force | ForEach-Object { $_.FullName.Substring($appDir.Length + 1) }) -join ', '
    Fail "the install folder still exists after the uninstall: $leftovers"
}
Assert-Same $newerDatabase (Get-DataListing) 'data folder listing after the uninstall (the uninstaller leaves user data exactly as it was)'
Pass "uninstalled; $appDir is gone; $dataDir still holds exactly what the gates wrote"

Write-Host 'All 5 recovery gates passed.'
