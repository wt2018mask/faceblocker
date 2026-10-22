#requires -Version 5.1
<#
Builds a self-contained Windows x64 folder and ZIP for VeilDesk.

The script intentionally keeps the ONNX weights outside the executable:
they are copied from models/ into the publish folder, hash-verified against
models/manifest.json, and never downloaded by the app at runtime.

Example:
  powershell -ExecutionPolicy Bypass -File scripts/package.ps1
  powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -SkipZip
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$SkipZip,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publishDir = if ([string]::IsNullOrWhiteSpace($Output)) {
    Join-Path $artifacts "VeilDesk-$Runtime"
} else {
    [System.IO.Path]::GetFullPath($Output)
}
$project = Join-Path $root "src\VeilDesk.App\VeilDesk.App.csproj"
$models = Join-Path $root "models"
$builtinPacks = Join-Path $root "builtin-packs"
$zipPath = if ($Output) { $publishDir + '.zip' } else { Join-Path $artifacts "VeilDesk-1.0.0-$Runtime.zip" }
if ((Test-Path -LiteralPath $publishDir) -and
    @((Get-ChildItem -LiteralPath $publishDir -Force -ErrorAction Stop)).Count -gt 0) {
    throw "Preserving existing publish folder: $publishDir. Pass -Output with a new path."
}
if (-not $SkipZip -and (Test-Path -LiteralPath $zipPath)) { throw "Preserving existing archive: $zipPath. Pass -Output with a new path." }

function Require-File([string]$Path, [string]$Hint = "") {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing required file: $Path`n$Hint"
    }
}

Write-Host "==> Checking prerequisites"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw "dotnet was not found on PATH." }
& dotnet --version | Out-Host
Require-File $project
Require-File (Join-Path $root "LICENSE")
Require-File (Join-Path $root "README.md")
Require-File (Join-Path $models "manifest.json") "Run scripts/fetch-models.ps1 first."

$manifest = Get-Content (Join-Path $models "manifest.json") -Raw | ConvertFrom-Json
foreach ($entry in $manifest.models) {
    $file = Join-Path $models $entry.file
    Require-File $file "Run scripts/fetch-models.ps1 first."
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($hash -ne $entry.sha256) {
        throw "Model hash mismatch for $($entry.file).`n  expected $($entry.sha256)`n  actual   $hash"
    }
    Write-Host ("  verified {0} ({1:N0} bytes, {2})" -f $entry.file, (Get-Item $file).Length, $entry.license)
}

New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "==> Publishing $Configuration / $Runtime (self-contained)"
[string[]]$restoreArgs = if ($NoRestore) { @('--no-restore') } else { @() }
& dotnet publish $project @restoreArgs `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Copying models, packs, documentation and licence"
$targetModels = Join-Path $publishDir "models"
New-Item -ItemType Directory -Force -Path $targetModels | Out-Null
foreach ($entry in $manifest.models) {
    Copy-Item -LiteralPath (Join-Path $models $entry.file) -Destination $targetModels -Force
}
Copy-Item -LiteralPath (Join-Path $models "manifest.json") -Destination $targetModels -Force
Copy-Item -LiteralPath (Join-Path $models "README.md") -Destination $targetModels -Force
Copy-Item -LiteralPath (Join-Path $models "YUNET-LICENSE.txt") -Destination $targetModels -Force

$targetPacks = Join-Path $publishDir "builtin-packs"
New-Item -ItemType Directory -Force -Path $targetPacks | Out-Null
Get-ChildItem -LiteralPath $builtinPacks -Filter *.veilpack | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $targetPacks -Force
}

$targetDocs = Join-Path $publishDir "docs"
New-Item -ItemType Directory -Force -Path $targetDocs | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root "docs") -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $targetDocs -Force
}
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $publishDir -Force
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.md") -Destination $publishDir -Force

$exe = Join-Path $publishDir "VeilDesk.App.exe"
Require-File $exe
foreach ($name in @(
    "builtin-packs\builtin.ink-shield.veilpack",
    "models\nudenet-320n.onnx",
    "models\yolov8n-320.onnx",
    "models\selfie_segmentation.onnx",
    "docs\KNOWN_LIMITATIONS.md",
    "LICENSE"
)) {
    Require-File (Join-Path $publishDir $name)
}

$buildInfo = @"
VeilDesk 1.0.0
Release status: engineering preview; live protection acceptance gates pending
Source revision: $(& git -C $root rev-parse HEAD)
Built (UTC): $([DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))
Configuration: $Configuration
Runtime: $Runtime (self-contained, win-x64)
SDK: $(& dotnet --version)
Models: NudeNet 320n (AGPL-3.0), YOLOv8n 320/480 (AGPL-3.0),
        MediaPipe Selfie Segmentation (Apache-2.0)
No model is downloaded at runtime. Verify models/manifest.json hashes.
"@
Set-Content -LiteralPath (Join-Path $publishDir "BUILD-INFO.txt") -Value $buildInfo -Encoding UTF8

Write-Host "==> Writing SHA256SUMS.txt"
$checksumPath = Join-Path $publishDir "SHA256SUMS.txt"
$lines = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.FullName -ne $checksumPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($publishDir.Length + 1)
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
$lines | Set-Content -LiteralPath $checksumPath -Encoding ASCII

$size = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("==> Publish folder: {0} ({1:N1} MB)" -f $publishDir, ($size / 1MB))

if (-not $SkipZip) {
    Write-Host "==> Creating ZIP"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDir, $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Host ("==> ZIP: {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
    Write-Host "    SHA256 $zipHash"
    # Stored outside the archive so documentation never has to embed the hash
    # of the archive that contains it (a self-referential, always-stale claim).
    $hashFile = if ($Output) { $zipPath + '.sha256' } else { Join-Path $root "PACKAGE-SHA256.txt" }
    "$zipHash  $(Split-Path -Leaf $zipPath)" |
        Set-Content -LiteralPath $hashFile -Encoding ASCII
    Write-Host "    hash file: $hashFile"
}

Write-Host ""
Write-Host "Package ready. Run VeilDesk.App.exe from the publish folder."
Write-Host "Data is stored under %LOCALAPPDATA%\VeilDesk unless VEILDESK_HOME is set."
