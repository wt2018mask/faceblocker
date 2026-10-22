#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { (Get-Command dotnet).Source } else { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
Push-Location $root
try {
    & $dotnet test tests/VeilDesk.Core.Tests/VeilDesk.Core.Tests.csproj --no-restore --nologo -p:RunAnalyzers=false
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $LASTEXITCODE" }
    & $dotnet build src/VeilDesk.App/VeilDesk.App.csproj -c Release --no-restore --nologo -p:RunAnalyzers=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    & $dotnet run --project tools/ProtectionProbe/ProtectionProbe.csproj -c Release -p:RunAnalyzers=false
    if ($LASTEXITCODE -ne 0) { throw "Protection probe failed: $LASTEXITCODE" }
    & powershell -NoProfile -ExecutionPolicy Bypass -STA -File scripts/Show-TestSubject.ps1 -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw "Viewer control validation failed: $LASTEXITCODE" }
} finally { Pop-Location }
