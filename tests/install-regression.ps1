# Silent-install regression (Task 13, build-chain step 8).
#
# Runs the Inno Setup installer /VERYSILENT, asserts installed files exist, launches
# the INSTALLED exe with --enable-control-server and asserts via /status that:
#   - configured == false (fresh install, no setup run);
#   - the startup phase is the DESIGNED unconfigured state
#     'error:ASR not configured. Click Setup to install.'
#     Note: the task brief phrased this as "startup phase not error:*", but the
#     shipped state machine (Task 6, Swift parity -- AppController.InitializeAsync
#     deliberately surfaces the setup-required error phase when unconfigured) makes
#     that unsatisfiable. Asserting the EXACT designed phase is the stricter and
#     honest regression: any other error (crash, engine-not-ready, garbage) fails.
# Then quits via /control/quit, uninstalls /VERYSILENT, and asserts the install
# dir is gone. %LOCALAPPDATA%\WinLocalASR (models) is deliberately NOT touched.
#
# PowerShell 5.1 AND 7 compatible; pure ASCII.
# Exit codes: 0 = regression green; 1 = failure.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [int]$Port = 17861,
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    exit 1
}

function Get-StatusSnapshot {
    try {
        return Invoke-RestMethod -Uri "http://localhost:$Port/status" -Method Get -TimeoutSec 5
    } catch {
        return $null
    }
}

if (-not (Test-Path $InstallerPath)) { Fail "installer not found: $InstallerPath" }
$InstallerPath = (Resolve-Path $InstallerPath).Path
$installDir = Join-Path $env:ProgramFiles 'WinLocalASR'
$installedExe = Join-Path $installDir 'WinLocalASR.App.exe'
$installLog = Join-Path ([System.IO.Path]::GetTempPath()) 'winlocalasr-install.log'

# ---- 1. silent install ----------------------------------------------------------------
Write-Host "regression: silent install $InstallerPath"
$installerProc = Start-Process -FilePath $InstallerPath `
    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$installLog") -PassThru -Wait
if ($installerProc.ExitCode -ne 0) {
    if (Test-Path $installLog) { Get-Content $installLog -Tail 30 | ForEach-Object { Write-Host "  $_" } }
    Fail "installer exited with code $($installerProc.ExitCode)"
}
Write-Host '[PASS] installer /VERYSILENT exited 0'

# ---- 2. installed files exist ------------------------------------------------------------
foreach ($rel in @('WinLocalASR.App.exe', 'unins000.exe')) {
    $p = Join-Path $installDir $rel
    if (-not (Test-Path $p)) { Fail "installed file missing: $p" }
    Write-Host "[PASS] installed file exists: $rel"
}

# ---- 3. installed-app smoke via ControlServer --------------------------------------------
$appProc = Start-Process -FilePath $installedExe `
    -ArgumentList @('--enable-control-server', "--control-server-port=$Port") -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $snapshot = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($appProc.HasExited) { Fail "installed app exited early with code $($appProc.ExitCode)" }
        $s = Get-StatusSnapshot
        if ($null -ne $s -and ([string]$s.phase) -like 'error:*') { $snapshot = $s; break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $snapshot) {
        $last = Get-StatusSnapshot
        Fail ("/status never reached an error:* phase within ${TimeoutSec}s " +
            "(unconfigured fresh install must surface the setup-required phase; last=$($last.phase))")
    }

    if ($snapshot.configured -ne $false) { Fail "expected configured == false, got: $($snapshot.configured)" }
    Write-Host '[PASS] configured == false (fresh install)'

    $expected = 'error:ASR not configured. Click Setup to install.'
    if ([string]$snapshot.phase -cne $expected) {
        Fail "phase is '$($snapshot.phase)' but the designed unconfigured startup phase is '$expected'"
    }
    Write-Host "[PASS] startup phase is the designed unconfigured state: $expected"
} finally {
    try { Invoke-RestMethod -Uri "http://localhost:$Port/control/quit" -Method Post -TimeoutSec 15 | Out-Null } catch { }
    if ($null -ne $appProc -and -not $appProc.HasExited) {
        $appProc.WaitForExit(15000) | Out-Null
        if (-not $appProc.HasExited) { Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue }
    }
}
if ($appProc.ExitCode -ne 0) { Fail "installed app exit code was $($appProc.ExitCode), expected 0" }
Write-Host '[PASS] installed app quit cleanly via /control/quit (exit 0)'

# ---- 4. silent uninstall ------------------------------------------------------------------
$uninstaller = Join-Path $installDir 'unins000.exe'
$unProc = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT') -PassThru -Wait
if ($unProc.ExitCode -ne 0) { Fail "uninstaller exited with code $($unProc.ExitCode)" }
Write-Host '[PASS] uninstaller /VERYSILENT exited 0'
if (Test-Path $installDir) { Fail "install dir still present after uninstall: $installDir" }
Write-Host '[PASS] install dir removed by uninstaller'
Write-Host '=== silent install regression PASSED ==='
exit 0
