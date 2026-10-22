#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Interactive,
    [string]$ImagePath,
    [string]$AppPath,
    [ValidateRange(5, 300)][int]$DurationSeconds = 60
)
$ErrorActionPreference = 'Stop'
if (-not $Interactive) { throw 'No UI started. Read docs/RECOVERY-20261022.md and pass -Interactive deliberately.' }
if (-not $ImagePath -or -not $AppPath) { throw 'Pass explicit -ImagePath and -AppPath.' }
$resolvedImage = (Resolve-Path -LiteralPath $ImagePath).ProviderPath
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).ProviderPath
if (Get-Process VeilDesk.App -ErrorAction SilentlyContinue) { throw 'An existing VeilDesk session is running. Close it yourself before starting this isolated test.' }
$viewerScript = Join-Path $PSScriptRoot 'Show-TestSubject.ps1'
$viewer = $null
$testApp = $null
$previousRoot = $env:VEILDESK_HOME
try {
    $env:VEILDESK_HOME = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\interactive-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $env:VEILDESK_HOME -Force | Out-Null
    $viewerArgs = '-NoProfile -ExecutionPolicy Bypass -STA -File "{0}" -Interactive -ImagePath "{1}" -DurationSeconds {2}' -f $viewerScript, $resolvedImage, $DurationSeconds
    $viewer = Start-Process powershell -WindowStyle Hidden -ArgumentList $viewerArgs -PassThru `
        -RedirectStandardOutput (Join-Path $env:VEILDESK_HOME 'viewer.out.txt') `
        -RedirectStandardError (Join-Path $env:VEILDESK_HOME 'viewer.err.txt')
    $testApp = Start-Process -FilePath $resolvedApp -PassThru
    Write-Host 'Press Start manually in VeilDesk. Check mask alignment, Stop during fail-closed, and repeated Start/Stop.'
    Write-Host "Bounded test: $DurationSeconds seconds. Ctrl+C closes only the two processes created here."
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds -and -not $testApp.HasExited) {
        Start-Sleep -Milliseconds 200
        $testApp.Refresh()
    }
} finally {
    foreach ($owned in @($testApp, $viewer)) {
        if ($null -ne $owned -and -not $owned.HasExited) {
            [void]$owned.CloseMainWindow()
            if (-not $owned.WaitForExit(3000)) { Stop-Process -Id $owned.Id -ErrorAction SilentlyContinue }
        }
    }
    $env:VEILDESK_HOME = $previousRoot
}
