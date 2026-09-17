# Self-contained bundle verification (Task 13, build-chain step 3 -- QA-).
#
# Method (documented per the task contract): with PublishSingleFile=true the .NET
# runtime ships INSIDE the exe. We therefore verify:
#   1. the publish directory holds no loose DLLs (a framework-dependent publish
#      would scatter System.*.dll / coreclr.dll next to the exe);
#   2. the exe bytes contain runtime markers: System.Private.CoreLib.dll (managed
#      core runtime assembly -- bundle-manifest ASCII entry; never present in a
#      framework-dependent payload) and the native runtime components
#      coreclr.dll + hostpolicy.dll. NOTE from the first CI iteration: in a .NET 10
#      single-file superhost the managed entries appear as plain-ASCII bundle
#      manifest strings, while the native runtime components appear only as
#      UTF-16LE strings inside the superhost -- so both encodings are searched
#      (UTF-16LE decoded from even and odd byte offsets to cover alignment);
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

# Managed core runtime assembly: ASCII bundle-manifest entry.
if ($text.Contains('System.Private.CoreLib.dll')) {
    Write-Host '[PASS] bundle contains embedded runtime marker (ASCII manifest): System.Private.CoreLib.dll'
} else {
    Write-Host '[FAIL] bundle lacks ASCII runtime marker: System.Private.CoreLib.dll (runtime NOT embedded?)'
    $failed = $true
}

# Native runtime components: UTF-16LE strings inside the superhost (see header).
# Decode from both even and odd byte offsets so either alignment is found.
$lenEven = $bytes.Length - ($bytes.Length % 2)
$textEven = [System.Text.Encoding]::Unicode.GetString($bytes, 0, $lenEven)
$lenOddBytes = $bytes.Length - 1
$lenOdd = $lenOddBytes - ($lenOddBytes % 2)
$textOdd = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $lenOdd)
foreach ($marker in @('coreclr.dll', 'hostpolicy.dll')) {
    if ($textEven.Contains($marker) -or $textOdd.Contains($marker)) {
        Write-Host "[PASS] bundle contains embedded runtime marker (UTF-16LE): $marker"
    } else {
        Write-Host "[FAIL] bundle lacks UTF-16LE runtime marker: $marker (runtime NOT embedded?)"
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
