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
$viewerExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\SubjectViewer\bin\Release\net8.0-windows10.0.19041.0\SubjectViewer.exe'
if (-not (Test-Path -LiteralPath $viewerExe)) { throw 'Build tools/SubjectViewer -c Release before interactive validation.' }
$viewer = $null
$testApp = $null
$previousRoot = $env:VEILDESK_HOME
try {
    $env:VEILDESK_HOME = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts\interactive-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $env:VEILDESK_HOME -Force | Out-Null
    $viewerArgs = '--interactive "{0}" {1}' -f $resolvedImage, $DurationSeconds
    # This is the deliberately requested visible test tool, with no hidden shell host.
    $viewer = Start-Process $viewerExe -WindowStyle Normal -ArgumentList $viewerArgs -PassThru `
        -RedirectStandardOutput (Join-Path $env:VEILDESK_HOME 'viewer.out.txt') `
        -RedirectStandardError (Join-Path $env:VEILDESK_HOME 'viewer.err.txt')
    $testApp = Start-Process -FilePath $resolvedApp -PassThru
    Write-Host 'Press Start manually in VeilDesk. Check mask alignment, Stop during fail-closed, and repeated Start/Stop.'
    Write-Host "Bounded test: $DurationSeconds seconds. Ctrl+C closes only the two processes created here."
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastSample = -1
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds -and -not $testApp.HasExited) {
        Start-Sleep -Milliseconds 200
        $testApp.Refresh()
        $sample = [int][Math]::Floor($clock.Elapsed.TotalSeconds)
        if (-not $testApp.HasExited -and $sample -gt $lastSample) {
            $lastSample = $sample
            @{ seconds=$sample; cpuSeconds=$testApp.TotalProcessorTime.TotalSeconds; privateBytes=$testApp.PrivateMemorySize64;
                workingSetBytes=$testApp.WorkingSet64; handles=$testApp.HandleCount; threads=$testApp.Threads.Count } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $env:VEILDESK_HOME 'resources.jsonl')
        }
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
