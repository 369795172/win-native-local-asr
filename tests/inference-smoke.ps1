# Real-inference smoke (Task 13 job B; Task 14's precondition gate).
#
# Per repo-root versions.json: downloads the pinned llama.cpp win-x64 asset (SHA256
# verified via verify-llama-binaries.ps1) and the pinned Q8_0 GGUF pair (~2.5 GB;
# huggingface.co primary with hf-mirror.com host-swap fallback per SetupRunner
# semantics, SHA256 verified per file), starts llama-server.exe with the rfc.md
# "Inference Contract" CLI (-m <main> --mmproj <mmproj> --host 127.0.0.1 --port <P>),
# ready-polls GET /health (bounded, generous -- a 2.1 GB model load takes tens of
# seconds), then POSTs BOTH tests/assets/spike-sample.wav (1.4 s) and
# tests/assets/spike-sample-10s.wav (8.7 s) via multipart to
# /v1/audio/transcriptions. Asserts each response's text -- AFTER stripping the
# `language <Lang><asr_text>` artifact prefix -- is non-empty and artifact-free.
# Raw responses are logged for forensics; the assertion is on the STRIPPED text.
#
# PowerShell 5.1 AND 7 compatible; pure ASCII.
# Exit codes: 0 = both transcriptions verified; 1 = failure.

[CmdletBinding()]
param(
    [string]$ManifestPath = 'versions.json',
    [string]$WorkDir = 'smoke-work',
    [int]$Port = 18234,
    [int]$ReadyTimeoutSec = 600,
    [int]$PerRequestTimeoutSec = 600
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ManifestPath)) { Fail "manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ---- engine binaries (pinned zip, sha-verified, whole-zip extract) --------------------
& (Join-Path $PSScriptRoot 'verify-llama-binaries.ps1') -ManifestPath $ManifestPath -WorkDir $WorkDir
if ($LASTEXITCODE -ne 0) { Fail 'llama.cpp binary verification failed (see log above)' }
$serverExe = Join-Path $WorkDir 'llama-server.exe'

# ---- pinned GGUF pair: huggingface primary, hf-mirror host-swap fallback ---------------
function Get-ModelFile([string]$Repo, [string]$File, [string]$Revision, [string]$PinnedSha) {
    $dest = Join-Path $WorkDir $File
    if (Test-Path $dest) {
        $have = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($have -eq $PinnedSha.ToLower()) {
            Write-Host "smoke: $File already present with matching sha256 -- reusing"
            return $dest
        }
        Write-Host "smoke: $File present but sha mismatch -- redownloading"
        Remove-Item $dest -Force
    }

    $hfBase = ([string]$script:manifest.urls.huggingface).TrimEnd('/')
    $mirrorBase = ([string]$script:manifest.urls.hfMirror).TrimEnd('/')
    $primary = '{0}/{1}/resolve/{2}/{3}' -f $hfBase, $Repo, $Revision, $File
    $mirror = $primary.Replace($hfBase, $mirrorBase)

    foreach ($url in @($primary, $mirror)) {
        Write-Host "smoke: downloading $url"
        & curl.exe -L --fail --show-error --retry 3 --retry-delay 10 --connect-timeout 60 -o $dest $url
        if ($LASTEXITCODE -ne 0) {
            Write-Host "smoke: download failed (exit $LASTEXITCODE) -- trying next source"
            if (Test-Path $dest) { Remove-Item $dest -Force }
            continue
        }
        $sha = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($sha -ne $PinnedSha.ToLower()) {
            Write-Host "smoke: sha256 mismatch from $url (actual=$sha) -- trying next source"
            Remove-Item $dest -Force
            continue
        }
        Write-Host "[PASS] $File sha256 verified ($sha)"
        return $dest
    }
    Fail "could not download + verify $File from primary or mirror"
}

$mainGguf = Get-ModelFile $manifest.models.main.repo $manifest.models.main.file $manifest.models.main.revision $manifest.models.main.sha256
$mmprojGguf = Get-ModelFile $manifest.models.mmproj.repo $manifest.models.mmproj.file $manifest.models.mmproj.revision $manifest.models.mmproj.sha256

# ---- start llama-server (contract CLI) --------------------------------------------------
$stdoutLog = Join-Path $WorkDir 'server-out.log'
$stderrLog = Join-Path $WorkDir 'server-err.log'
Write-Host "smoke: starting llama-server on 127.0.0.1:$Port (model load takes a while)"
$proc = Start-Process -FilePath $serverExe `
    -ArgumentList @('-m', $mainGguf, '--mmproj', $mmprojGguf, '--host', '127.0.0.1', '--port', "$Port") `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

try {
    # ---- ready-poll /health (never a fixed sleep) ---------------------------------------
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSec)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) {
            Write-Host 'smoke: llama-server exited early. stderr tail:'
            if (Test-Path $stderrLog) { Get-Content $stderrLog -Tail 30 | ForEach-Object { Write-Host "  $_" } }
            Fail "llama-server exited with code $($proc.ExitCode) before becoming healthy"
        }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -Method Get -TimeoutSec 5
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { Fail "llama-server not healthy within ${ReadyTimeoutSec}s" }
    Write-Host '[PASS] /health ok after poll (model loaded)'

    # ---- POST both spike WAVs, assert on the STRIPPED text -------------------------------
    $samples = @(
        @{ Path = (Join-Path $PSScriptRoot 'assets/spike-sample.wav');     Label = 'spike-sample.wav (1.4 s)' },
        @{ Path = (Join-Path $PSScriptRoot 'assets/spike-sample-10s.wav'); Label = 'spike-sample-10s.wav (8.7 s)' }
    )
    foreach ($sample in $samples) {
        if (-not (Test-Path $sample.Path)) { Fail "sample wav missing: $($sample.Path)" }
        $url = "http://127.0.0.1:$Port/v1/audio/transcriptions"
        $raw = (& curl.exe -s --max-time $PerRequestTimeoutSec -X POST -F "file=@$($sample.Path);type=audio/wav" $url 2>&1 | Out-String).Trim()
        Write-Host "smoke: $($sample.Label) RAW RESPONSE (forensics): $raw"
        if ($LASTEXITCODE -ne 0) { Fail "curl POST failed for $($sample.Label) (exit $LASTEXITCODE)" }
        try {
            $resp = $raw | ConvertFrom-Json
        } catch {
            Fail "response is not JSON for $($sample.Label): $raw"
        }
        $text = [string]$resp.text
        $stripped = ($text -replace '^language\s+[^<]*<asr_text>', '').Trim()
        if ([string]::IsNullOrWhiteSpace($stripped)) {
            Fail "$($sample.Label) stripped transcript is EMPTY (raw text: $text)"
        }
        if (($stripped -match '<asr_text>') -or ($stripped -match '^language\s+')) {
            Fail "$($sample.Label) stripped transcript still carries the artifact prefix (stripped: $stripped)"
        }
        Write-Host "[PASS] $($sample.Label) stripped transcript non-empty and artifact-free: [$stripped]"
    }
} finally {
    if ($null -ne $proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '=== inference smoke PASSED ==='
exit 0
