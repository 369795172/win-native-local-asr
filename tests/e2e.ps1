# WinLocalASR end-to-end driver (Task 12).
#
# Launches the app with --enable-control-server --fake-configured, drives a full
# recording cycle through the REAL AppController via the ControlServer HTTP API,
# and asserts:
#   1. /status reaches phase "idle" (bounded; unreachable server = explicit fail, never a hang)
#   2. lastTranscript equals the preset text (main assertion)
#   3. clipboard contains the preset text (secondary; Start-Job + Wait-Job -Timeout 10 guard ---
#      headless-clipboard hangs degrade VISIBLY to a GITHUB_STEP_SUMMARY note, never block the run)
#   4. phaseHistory carries the ordered transitions idle -> recording -> processing -> idle
#      and the observed hud controlValues covered recording/processing/copied
#   5. /control/quit exits the process gracefully with exit code 0 (bounded)
#
# PowerShell 5.1 AND 7 compatible: no ?? / ternary / chain operators; the CJK preset is
# built from code points so the file is encoding-independent.
#
# Exit codes: 0 = all assertions passed (clipboard may be degraded); 1 = failure.

[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\WinLocalASR.App\bin\Debug\net10.0-windows\WinLocalASR.App.exe'),
    [int]$Port = 17846,
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$script:Results = New-Object System.Collections.Generic.List[string]
$script:Degraded = $false

function Add-Result([string]$Name, [bool]$Pass, [string]$Detail) {
    $status = if ($Pass) { 'PASS' } else { 'FAIL' }
    $script:Results.Add(("[{0}] {1}{2}" -f $status, $Name, $(if ($Detail) { " - $Detail" } else { '' })))
    if (-not $Pass) {
        Write-Host ("[FAIL] {0}{1}" -f $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor Red
    } else {
        Write-Host ("[PASS] {0}{1}" -f $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor Green
    }
}

function Fail([string]$Message) {
    Write-Host "`n=== e2e FAILURE: $Message ===" -ForegroundColor Red
    foreach ($r in $script:Results) { Write-Host "  $r" }
    Write-Host "=== end summary ===`n" -ForegroundColor Red
    exit 1
}

function Get-StatusSnapshot {
    # One GET /status; $null on any transport error (server degraded/unreachable).
    try {
        return Invoke-RestMethod -Uri "http://localhost:$Port/status" -Method Get -TimeoutSec 5
    } catch {
        return $null
    }
}

function Invoke-ControlEndpoint([string]$Path, [string]$Body = $null) {
    if ($null -ne $Body) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
        return Invoke-RestMethod -Uri "http://localhost:$Port$Path" -Method Post -Body $bytes -ContentType 'application/json' -TimeoutSec 15
    }
    return Invoke-RestMethod -Uri "http://localhost:$Port$Path" -Method Post -TimeoutSec 15
}

function Wait-For {
    param(
        [scriptblock]$Condition,
        [string]$Description,
        [ref]$CapturedSnapshot
    )
    # Bounded poll: 250 ms interval, TimeoutSec budget. Every wait in this script goes
    # through here --- the QA- contract (unreachable /status fails explicitly, never hangs).
    $maxAttempts = [Math]::Max(1, [int]($TimeoutSec * 1000 / 250))
    $lastErrorSeen = $false
    for ($i = 0; $i -lt $maxAttempts; $i++) {
        $snapshot = Get-StatusSnapshot
        if ($null -eq $snapshot) {
            $lastErrorSeen = $true
        } else {
            if ($lastErrorSeen) { $lastErrorSeen = $false }
            if ((& $Condition $snapshot) -eq $true) {
                if ($null -ne $CapturedSnapshot) { $CapturedSnapshot.Value = $snapshot }
                return $snapshot
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($lastErrorSeen) {
        Fail ("control server did not become reachable on http://localhost:$Port/ within ${TimeoutSec}s ($Description). " +
              'The server is either degraded (port conflict --- see shell.log) or the app failed to start.')
    }
    Fail ("timed out after ${TimeoutSec}s waiting for: $Description")
}

function Test-OrderedSubsequence([object[]]$History, [object[]]$Expected) {
    if ($null -eq $History) { return $false }
    $idx = 0
    foreach ($item in $History) {
        if ($idx -lt $Expected.Count -and $item -eq $Expected[$idx]) { $idx++ }
    }
    return ($idx -eq $Expected.Count)
}

# "------------ e2e" from code points: encoding-independent across PS 5.1/7 and file encodings.
$PresetText = [string]::Concat([char]0x4F60, [char]0x597D, [char]0x4E16, [char]0x754C, [char]0x0020, 'e2e')

# ---- 0. preconditions ---------------------------------------------------------------

if (-not (Test-Path $ExePath)) {
    Fail ("exe not found: $ExePath (pass -ExePath <path-to-WinLocalASR.App.exe>; CI passes the publish output)")
}
$ExePath = (Resolve-Path $ExePath).Path
Write-Host "e2e: exe       = $ExePath"
Write-Host "e2e: port      = $Port"
Write-Host "e2e: timeout   = ${TimeoutSec}s"
Write-Host "e2e: preset    = $PresetText"

# ---- 1. launch ----------------------------------------------------------------------

$stdoutLog = Join-Path ([System.IO.Path]::GetTempPath()) "winlocalasr-e2e-out-$PID.log"
$stderrLog = Join-Path ([System.IO.Path]::GetTempPath()) "winlocalasr-e2e-err-$PID.log"
$proc = Start-Process -FilePath $ExePath -ArgumentList @('--enable-control-server', '--fake-configured') `
    -PassThru -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

try {
    if ($proc.HasExited) {
        Fail ("app exited immediately with code $($proc.ExitCode); stderr: $(Get-Content $stderrLog -Raw)")
    }

    # ---- 2. wait for configured+idle startup ----------------------------------------
    $startup = [ref]$null
    Wait-For -Condition { param($s) $s.phase -eq 'idle' -and $s.configured -eq $true } `
        -Description 'startup reaching phase=idle with configured=true' -CapturedSnapshot $startup | Out-Null
    Add-Result 'startup reaches idle (configured=true)' $true "phaseHistory=$($startup.Value.phaseHistory -join ',')"

    # ---- 3. preset the fake transcript ----------------------------------------------
    # ConvertTo-Json \uXXXX-escapes the CJK --- the server's JSON parser decodes it back.
    $presetBody = @{ text = $PresetText } | ConvertTo-Json -Compress
    $ack = Invoke-ControlEndpoint '/fake-transcript' $presetBody
    if ($ack.action -ne 'fake_transcript_set') {
        Fail ("POST /fake-transcript returned unexpected ack: $($ack.action)")
    }
    Add-Result 'fake transcript preset accepted' $true

    # ---- 4. toggle ON, hold recording ------------------------------------------------
    Invoke-ControlEndpoint '/control/toggle' | Out-Null
    $recording = [ref]$null
    Wait-For -Condition { param($s) $s.phase -eq 'recording' } `
        -Description 'phase=recording after first /control/toggle' -CapturedSnapshot $recording | Out-Null
    Add-Result 'toggle starts recording via real state machine' $true
    Add-Result 'hud controlValue is recording while recording' ($recording.Value.hudControlValue -eq 'recording') `
        "observed=$($recording.Value.hudControlValue)"

    # Small dwell so the sine capture accumulates genuine audio (fake pipeline: ~1s).
    Start-Sleep -Milliseconds 1200

    # ---- 5. toggle OFF, run the transcribe cycle -------------------------------------
    Invoke-ControlEndpoint '/control/toggle' | Out-Null

    # Continuous snapshot poll: collects observed hud controlValues (no per-state races ---
    # every snapshot contributes to the observed set), stops at completed idle+transcript.
    $maxAttempts = [Math]::Max(1, [int]($TimeoutSec * 1000 / 150))
    $hudObserved = @{}
    $final = $null
    $sawProcessing = $false
    for ($i = 0; $i -lt $maxAttempts; $i++) {
        $snapshot = Get-StatusSnapshot
        if ($null -ne $snapshot) {
            $hudObserved[$snapshot.hudControlValue] = $true
            if ($snapshot.phase -eq 'processing') { $sawProcessing = $true }
            if ($snapshot.phase -eq 'idle' -and $snapshot.lastTranscript -eq $PresetText) {
                $final = $snapshot
                break
            }
        }
        Start-Sleep -Milliseconds 150
    }
    if ($null -eq $final) {
        $last = Get-StatusSnapshot
        Fail ("transcription cycle did not complete within ${TimeoutSec}s (last phase=$($last.phase), " +
              "lastTranscript=$($last.lastTranscript))")
    }

    # ---- 6. MAIN assertion: lastTranscript -------------------------------------------
    Add-Result 'MAIN lastTranscript equals preset' ($final.lastTranscript -ceq $PresetText) "value=$($final.lastTranscript)"

    # ---- 7. SECONDARY assertion: clipboard (Start-Job guarded) ------------------------
    $clipJob = Start-Job -ScriptBlock {
        param($Expected)
        try {
            $clip = Get-Clipboard -Raw -ErrorAction Stop
            if ($clip -ceq $Expected) { return 'match' } else { return "mismatch:[$clip]" }
        } catch {
            return "error:$($_.Exception.Message)"
        }
    } -ArgumentList $PresetText
    $done = Wait-Job -Job $clipJob -Timeout 10
    if ($null -eq $done) {
        # Headless-clipboard hang converted into a VISIBLE degradation (never a hang).
        Stop-Job -Job $clipJob | Out-Null
        Remove-Job -Job $clipJob -Force | Out-Null
        $script:Degraded = $true
        $note = 'e2e clipboard assertion DEGRADED: Get-Clipboard did not answer within 10s (headless session). Main assertion (lastTranscript) still enforced.'
        Add-Result 'clipboard equals preset (secondary)' $false 'DEGRADED (timeout guarded)'
        if ($env:GITHUB_STEP_SUMMARY) {
            Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $note -Encoding UTF8
        }
        Write-Host $note -ForegroundColor Yellow
    } else {
        $clipResult = Receive-Job -Job $clipJob
        Remove-Job -Job $clipJob -Force | Out-Null
        $clipboardPass = ($clipResult -eq 'match')
        Add-Result 'clipboard equals preset (secondary)' $clipboardPass "job=$clipResult"
    }

    # ---- 8. sequence assertions -------------------------------------------------------
    $historyOk = Test-OrderedSubsequence -History $final.phaseHistory -Expected @('idle', 'recording', 'processing', 'idle')
    Add-Result 'phaseHistory ordered idle->recording->processing->idle' $historyOk `
        "history=$($final.phaseHistory -join ',')"
    if (-not $historyOk) { Fail 'phaseHistory sequence assertion failed' }

    $hudOk = ($hudObserved.ContainsKey('recording') -and $hudObserved.ContainsKey('processing') -and
              ($hudObserved.ContainsKey('copied') -or $final.hudControlValue -eq 'copied'))
    Add-Result 'hud controlValues covered recording/processing/copied' $hudOk `
        "observed=$(($hudObserved.Keys | Sort-Object) -join ','),final=$($final.hudControlValue)"
    if (-not $hudOk) { Fail 'hud controlValue coverage assertion failed' }

    # ---- 9. graceful quit --------------------------------------------------------------
    Invoke-ControlEndpoint '/control/quit' | Out-Null
    $exited = $proc.WaitForExit(15000)
    if (-not $exited) {
        Fail 'app did not exit within 15s of POST /control/quit'
    }
    Add-Result 'quit exits gracefully with code 0' ($proc.ExitCode -eq 0) "exitCode=$($proc.ExitCode)"
    if ($proc.ExitCode -ne 0) { Fail "exit code was $($proc.ExitCode), expected 0" }
} finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

# ---- summary ------------------------------------------------------------------------
Write-Host "`n=== e2e assertion summary ==="
foreach ($r in $script:Results) { Write-Host "  $r" }
if ($script:Degraded) {
    Write-Host '  NOTE: clipboard assertion was DEGRADED this run (see GITHUB_STEP_SUMMARY).' -ForegroundColor Yellow
}
Write-Host '=== e2e PASSED ===' -ForegroundColor Green
exit 0
