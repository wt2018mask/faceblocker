#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ImagePath,
    [switch]$Interactive,
    [switch]$ValidateOnly,
    [ValidateRange(1, 300)][int]$DurationSeconds = 60
)
$ErrorActionPreference = 'Stop'
if (-not $Interactive -and -not $ValidateOnly) {
    throw 'No viewer started. Pass -Interactive -ImagePath <file> deliberately, or -ValidateOnly for headless control initialization.'
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -NoProfile -STA -File.' }

# Construct the actual WPF Image explicitly. No XAML name lookup, string or
# reused shell variable is allowed to stand in for the image control.
[System.Windows.Controls.Image]$subjectControl = [System.Windows.Controls.Image]::new()
$subjectControl.Stretch = [System.Windows.Media.Stretch]::Uniform
if ($ImagePath) {
    $resolvedImage = (Resolve-Path -LiteralPath $ImagePath).ProviderPath
    $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
    $bitmap.BeginInit()
    $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $bitmap.UriSource = [Uri]$resolvedImage
    $bitmap.EndInit()
    $bitmap.Freeze()
} else {
    if ($Interactive) { throw 'Interactive mode requires an explicit -ImagePath; there is no default test image.' }
    $bitmap = [System.Windows.Media.Imaging.BitmapSource]::Create(1, 1, 96, 96,
        [System.Windows.Media.PixelFormats]::Bgra32, $null, [byte[]](0, 0, 0, 255), 4)
}
$subjectControl.Source = $bitmap
if ($subjectControl.Source -ne $bitmap) { throw 'WPF image assignment failed.' }
if ($ValidateOnly) { Write-Output 'PASS WPF Image Source/Stretch initialization; no window created'; return }

$window = [System.Windows.Window]::new()
$window.Title = 'VeilDesk test subject - close or press Escape to exit'
$window.Width = 480
$window.Height = 640
$window.Content = $subjectControl
$window.Add_KeyDown({ if ($_.Key -eq 'Escape') { $window.Close() } })
$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromSeconds($DurationSeconds)
$timer.Add_Tick({ $window.Close() })
$window.Add_Closed({ $timer.Stop() })
try { $timer.Start(); [void]$window.ShowDialog() }
finally { $timer.Stop(); $window.Close(); $subjectControl.Source = $null }
