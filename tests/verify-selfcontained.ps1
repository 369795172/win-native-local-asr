# Self-contained bundle verification (Task 13, build-chain step 3 -- QA-).
#
# Method (documented per the task contract): with PublishSingleFile=true the .NET
# runtime ships INSIDE the exe -- the single-file bundle header lists every embedded
# file name as plain text. We therefore verify:
#   1. the publish directory holds no loose DLLs (a framework-dependent publish
#      would scatter System.*.dll / coreclr.dll next to the exe);
#   2. the exe bytes contain the runtime file-name markers coreclr.dll,
#      hostpolicy.dll and System.Private.CoreLib.dll (runtime bundled in-file);
#   3. the exe is comfortably larger than 50 MB (self-contained floor; a
#      framework-dependent apphost is a few MB).
# Functional proof comes from the subsequent e2e step, which runs this exact exe.
#
# PowerShell 5.1 AND 7 compatible; pure ASCII.
# Exit codes: 0 = self-contained verified; 1 = verification failed.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppExe
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $AppExe)) {
    Write-Host "[FAIL] app exe not found: $AppExe"
    exit 1
}
$AppExe = (Resolve-Path $AppExe).Path
$dir = Split-Path -Parent $AppExe
$failed = $false

# ---- 1. directory shape (evidence + loose-DLL guard) --------------------------------
Write-Host "selfcontained: publish dir listing ($dir):"
Get-ChildItem $dir | ForEach-Object { Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.Name) }

$loose = @(Get-ChildItem $dir -Filter '*.dll')
if ($loose.Count -gt 0) {
    Write-Host '[FAIL] loose DLLs present in publish dir (single-file bundle must embed them):'
    $loose | ForEach-Object { Write-Host "  $($_.Name)" }
    $failed = $true
} else {
    Write-Host '[PASS] no loose DLLs in publish dir (all managed+native assemblies bundled)'
}

# ---- 2. embedded runtime markers ------------------------------------------------------
$bytes = [System.IO.File]::ReadAllBytes($AppExe)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
foreach ($marker in @('coreclr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    if ($text.Contains($marker)) {
        Write-Host "[PASS] bundle contains embedded runtime marker: $marker"
    } else {
        Write-Host "[FAIL] bundle lacks runtime marker: $marker (runtime NOT embedded?)"
        $failed = $true
    }
}

# ---- 3. size floor --------------------------------------------------------------------
$sizeMb = $bytes.Length / 1MB
if ($sizeMb -gt 50) {
    Write-Host ("[PASS] exe size {0:N1} MB > 50 MB self-contained floor" -f $sizeMb)
} else {
    Write-Host ("[FAIL] exe size {0:N1} MB <= 50 MB -- looks framework-dependent" -f $sizeMb)
    $failed = $true
}

if ($failed) { exit 1 }
Write-Host '=== self-contained verification PASSED ==='
exit 0
