# Builds only the canonical project. Run with -Launch after interactive permission.
param([switch]$Launch)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$dotnet=Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnet)){$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
if(Get-Process VeilDesk.App -ErrorAction SilentlyContinue){throw 'An existing VeilDesk instance is running. Close it manually before building.'}
$project=Join-Path $repo 'src\VeilDesk.App\VeilDesk.App.csproj'
& $dotnet build $project -c Release --nologo -p:RunAnalyzers=false
if($LASTEXITCODE -ne 0){throw 'Canonical build failed.'}
$app=Join-Path $repo 'src\VeilDesk.App\bin\Release\net8.0-windows10.0.19041.0\VeilDesk.App.exe'
Write-Output "Latest development application: $app"
Write-Output "SHA256: $((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash)"
if($Launch){$env:DOTNET_ROOT=Split-Path -Parent $dotnet; Start-Process -FilePath $app -WindowStyle Normal}
