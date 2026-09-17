# Pinned llama.cpp Windows binary verification (Task 13, build-chain step 5).
#
# Downloads the llama.cpp asset pinned by versions.json (tag b10964, official GitHub
# release URL), verifies its SHA256 against the pinned digest (policy "official"),
# asserts the zip contains llama-server.exe, asserts the DLL inventory matches
# docs/rfc.md "Windows binary pin" (8 contract DLLs + EXACTLY the 14 ggml-cpu-*
# dispatch variants; extra tool exes like llama-cli are zip-mates, not contract),
# extracts the WHOLE zip (CPU dispatch DLLs are selected at load time), and runs
# `llama-server.exe --version` (proves executability; VC++ runtime independence is
# real-machine work -- runners ship VC++ and cannot construct a bare environment).
#
# PowerShell 5.1 AND 7 compatible; pure ASCII.
# Exit codes: 0 = verified; 1 = failure.

[CmdletBinding()]
param(
    [string]$ManifestPath = 'versions.json',
    [string]$WorkDir = 'llama-bin'
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "[FAIL] $Message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ManifestPath)) { Fail "manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$tag = $manifest.llamaCpp.tag
$asset = $manifest.llamaCpp.assetName
$pinnedSha = ([string]$manifest.llamaCpp.sha256).ToLower()
$zipUrl = ('{0}/ggml-org/llama.cpp/releases/download/{1}/{2}' -f
    ([string]$manifest.urls.github).TrimEnd('/'), $tag, $asset)

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$zipPath = Join-Path $WorkDir $asset

# ---- download (pinned GitHub release asset) ------------------------------------------
Write-Host "llama verify: url = $zipUrl"
& curl.exe -L --fail --show-error --retry 3 --retry-delay 5 -o $zipPath $zipUrl
if ($LASTEXITCODE -ne 0) { Fail "download failed: $zipUrl" }

# ---- sha256 (official digest pin) ------------------------------------------------------
$actualSha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Write-Host "llama verify: sha256 = $actualSha"
if ($actualSha -ne $pinnedSha) { Fail "sha256 mismatch: actual=$actualSha pinned=$pinnedSha" }
Write-Host "[PASS] sha256 matches pinned digest ($pinnedSha)"

# ---- zip inventory vs the rfc.md contract ---------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $zipPath).Path)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
} finally {
    $archive.Dispose()
}
Write-Host "llama verify: zip contains $($entries.Count) files:"
$entries | Sort-Object | ForEach-Object { Write-Host "  $_" }

if ($entries -notcontains 'llama-server.exe') { Fail 'zip does not contain llama-server.exe' }
Write-Host '[PASS] zip contains llama-server.exe'

$contractDlls = @(
    'llama-server-impl.dll', 'llama.dll', 'llama-common.dll', 'mtmd.dll',
    'ggml.dll', 'ggml-base.dll', 'ggml-rpc.dll', 'libomp.dll'
)
foreach ($dll in $contractDlls) {
    if ($entries -notcontains $dll) { Fail "zip missing contract DLL: $dll" }
}
Write-Host '[PASS] all 8 contract DLLs present (llama-server-impl/llama/llama-common/mtmd/ggml/ggml-base/ggml-rpc/libomp)'

$cpuVariants = @(
    'ggml-cpu-alderlake.dll',   'ggml-cpu-cannonlake.dll',  'ggml-cpu-cascadelake.dll',
    'ggml-cpu-cooperlake.dll',  'ggml-cpu-haswell.dll',     'ggml-cpu-icelake.dll',
    'ggml-cpu-ivybridge.dll',   'ggml-cpu-piledriver.dll',  'ggml-cpu-sandybridge.dll',
    'ggml-cpu-sapphirerapids.dll', 'ggml-cpu-skylakex.dll', 'ggml-cpu-sse42.dll',
    'ggml-cpu-x64.dll',         'ggml-cpu-zen4.dll'
)
$zipCpu = @($entries | Where-Object { $_ -like 'ggml-cpu-*.dll' } | Sort-Object)
$expectedCpu = $cpuVariants | Sort-Object
$cpuDiff = Compare-Object -ReferenceObject $expectedCpu -DifferenceObject $zipCpu
if ($null -ne $cpuDiff) {
    $cpuDiff | ForEach-Object { Write-Host ("  cpu-variant diff: {0} ({1})" -f $_.InputObject, $_.SideIndicator) }
    Fail 'ggml-cpu-* dispatch DLL set does not match the rfc.md inventory (14 variants)'
}
Write-Host '[PASS] ggml-cpu-* set is exactly the 14 rfc.md dispatch variants'

# ---- extract whole zip + execute -------------------------------------------------------
Expand-Archive -Path $zipPath -DestinationPath $WorkDir -Force
$serverExe = Join-Path $WorkDir 'llama-server.exe'
$versionOutput = (& $serverExe --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { Fail "llama-server.exe --version exited $LASTEXITCODE" }
Write-Host "[PASS] llama-server.exe --version exited 0: $versionOutput"
Write-Host '=== llama.cpp binary verification PASSED ==='
exit 0
