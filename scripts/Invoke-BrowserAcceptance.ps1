#requires -Version 5.1
[CmdletBinding()]
param([switch]$Interactive,[Parameter(Mandatory=$true)][string]$BrowserPath,[ValidateRange(30,600)][int]$DurationSeconds=300)
$ErrorActionPreference='Stop'
if(-not $Interactive){throw 'No windows started. Obtain explicit user permission and pass -Interactive.'}
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root 'artifacts\browser-face-fixture'
foreach($name in @('index.html','astronaut.png','moving.webm')){if(-not(Test-Path -LiteralPath (Join-Path $fixture $name))){throw 'Run tools/prepare_face_fixture.py first.'}}
$browserExe=(Resolve-Path -LiteralPath $BrowserPath).ProviderPath
$appExe=Join-Path $root 'src\VeilDesk.App\bin\Release\net8.0-windows10.0.19041.0\VeilDesk.App.exe'
if(-not(Test-Path -LiteralPath $appExe)){throw 'Build Release first.'}
if(Get-Process VeilDesk.App -ErrorAction SilentlyContinue){throw 'Preserving existing VeilDesk session. Stop it manually before an isolated test.'}
$python=Join-Path $root '.venv\Scripts\python.exe'
$run=Join-Path $root ('artifacts\browser-acceptance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$server=$null; $browser=$null; $app=$null
$previousHome=$env:VEILDESK_HOME; $previousDiag=$env:VEILDESK_ACCEPTANCE_DIAGNOSTICS; $previousDotnet=$env:DOTNET_ROOT
try {
    $env:VEILDESK_HOME=$run
    $env:VEILDESK_ACCEPTANCE_DIAGNOSTICS='1'
    $env:DOTNET_ROOT=Join-Path $env:USERPROFILE '.dotnet'
    $listener=New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $listener.Start(); $port=$listener.LocalEndpoint.Port; $listener.Stop()
    $serverArgs='-m http.server {0} --bind 127.0.0.1 --directory "{1}"' -f $port,$fixture
    $server=Start-Process $python -WindowStyle Hidden -ArgumentList $serverArgs -PassThru -RedirectStandardOutput (Join-Path $run 'server.out.txt') -RedirectStandardError (Join-Path $run 'server.err.txt')
    Start-Sleep -Milliseconds 500
    if($server.HasExited){throw 'Local fixture server failed.'}
    $profile=Join-Path $run 'browser-profile'
    $browserArgs='--user-data-dir="{0}" --no-first-run --no-default-browser-check --disable-background-networking --new-window "http://127.0.0.1:{1}/index.html"' -f $profile,$port
    $browser=Start-Process $browserExe -WindowStyle Normal -ArgumentList $browserArgs -PassThru
    $app=Start-Process $appExe -WindowStyle Normal -PassThru
    @{appPid=$app.Id;browserPid=$browser.Id;serverPid=$server.Id;url="http://127.0.0.1:$port/index.html";seconds=$DurationSeconds} | ConvertTo-Json | Set-Content (Join-Path $run 'run.json')
    Write-Host "TEST ROOT: $run"
    Write-Host 'VeilDesk: Alt+F selects Face-only black; Alt+S activates the actual Start/Stop/Cancel button. Browser: PageDown/PageUp, Alt+C click-through, Alt+V video, Alt+P play. Ctrl+Shift+F10 emergency stop.'
    $clock=[Diagnostics.Stopwatch]::StartNew();$last=-1
    while($clock.Elapsed.TotalSeconds -lt $DurationSeconds -and -not $app.HasExited){
        Start-Sleep -Milliseconds 200; $app.Refresh(); $second=[int][Math]::Floor($clock.Elapsed.TotalSeconds)
        if(-not $app.HasExited -and $second -gt $last){
            $last=$second
            @{seconds=$second;cpuSeconds=$app.TotalProcessorTime.TotalSeconds;privateBytes=$app.PrivateMemorySize64;workingSetBytes=$app.WorkingSet64;handles=$app.HandleCount;threads=$app.Threads.Count} | ConvertTo-Json -Compress | Add-Content (Join-Path $run 'resources.jsonl')
        }
    }
} finally {
    foreach($owned in @($app,$browser)){
        if($null -ne $owned -and -not $owned.HasExited){[void]$owned.CloseMainWindow();if(-not $owned.WaitForExit(3000)){Stop-Process -Id $owned.Id -ErrorAction SilentlyContinue}}
    }
    if($null -ne $server -and -not $server.HasExited){Stop-Process -Id $server.Id -ErrorAction SilentlyContinue}
    $env:VEILDESK_HOME=$previousHome;$env:VEILDESK_ACCEPTANCE_DIAGNOSTICS=$previousDiag;$env:DOTNET_ROOT=$previousDotnet
    Write-Host "Cleaned up owned test processes. Local evidence: $run"
}
