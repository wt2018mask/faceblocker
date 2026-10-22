#requires -Version 5.1
<#
Downloads the exact model weights VeilDesk was built and validated with.
The app itself never downloads anything: this is an install-time / developer
script. Every download is hash-checked before it is moved into models/.
#>
param(
    [string]$Python = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root "models"
New-Item -ItemType Directory -Force -Path $models | Out-Null

function Get-VerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [hashtable]$Headers = @{}
    )

    if (Test-Path $Destination) {
        $existing = (Get-FileHash $Destination -Algorithm SHA256).Hash
        if ($existing -eq $Sha256) {
            Write-Host "ok      $(Split-Path $Destination -Leaf) (already present)"
            return
        }
        throw "Existing file has the wrong hash: $Destination`n  expected $Sha256`n  actual   $existing`nMove it away before re-running."
    }

    $temp = "$Destination.part"
    Write-Host "fetch   $Url"
    Invoke-WebRequest -Uri $Url -OutFile $temp -Headers $Headers -UseBasicParsing
    $actual = (Get-FileHash $temp -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        Remove-Item $temp -Force
        throw "Hash mismatch for $Url`n  expected $Sha256`n  actual   $actual"
    }
    Move-Item -Force $temp $Destination
    Write-Host "ok      $(Split-Path $Destination -Leaf) [$((Get-Item $Destination).Length) bytes]"
}

Get-VerifiedFile `
    -Url "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx" `
    -Destination (Join-Path $models "face_detection_yunet_2023mar.onnx") `
    -Sha256 "8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4"

Get-VerifiedFile `
    -Url "https://api.github.com/repos/notAI-tech/NudeNet/releases/assets/176831997" `
    -Destination (Join-Path $models "nudenet-320n.onnx") `
    -Sha256 "C15D8273ADAD2D0A92F014CC69AB2D6C311A06777A55545F2C4EB46F51911F0F" `
    -Headers @{ Accept = "application/octet-stream" }

Get-VerifiedFile `
    -Url "https://huggingface.co/onnx-community/mediapipe_selfie_segmentation/resolve/main/onnx/model.onnx" `
    -Destination (Join-Path $models "selfie_segmentation.onnx") `
    -Sha256 "3241AC4AD8AA35BDAF33946776DB29F7C283A413AA0B0DACB9483594B4531AAD"

Get-VerifiedFile `
    -Url "https://github.com/ultralytics/assets/releases/download/v8.1.0/yolov8n.pt" `
    -Destination (Join-Path $models "yolov8n.pt") `
    -Sha256 "31E20DDE3DEF09E2CF938C7BE6FE23D9150BBBE503982AF13345706515F2EF95"

$needsExport = -not (Test-Path (Join-Path $models "yolov8n-320.onnx")) -or
                -not (Test-Path (Join-Path $models "yolov8n-480.onnx"))
if ($needsExport) {
    if ([string]::IsNullOrWhiteSpace($Python)) {
        $candidate = Get-Command python -ErrorAction SilentlyContinue
        if ($candidate) { $Python = $candidate.Source }
    }
    if ([string]::IsNullOrWhiteSpace($Python)) {
        Write-Warning "Python with ultralytics/torch/onnx is required to export yolov8n ONNX. Install those packages, then run: python tools/export_models.py"
    }
    else {
        Write-Host "export  YOLOv8n ONNX (320 and 480)"
        & $Python (Join-Path $root "tools\export_models.py")
        if ($LASTEXITCODE -ne 0) { throw "YOLO ONNX export failed with exit code $LASTEXITCODE" }
    }
}

$expected = @{
    "nudenet-320n.onnx"          = "C15D8273ADAD2D0A92F014CC69AB2D6C311A06777A55545F2C4EB46F51911F0F"
    "yolov8n-320.onnx"           = "995548C2D6DB15CC9418D037BA8EBABD46E029201B9C35E49CB92D8C825A8004"
    "yolov8n-480.onnx"           = "D030CCDE37BC8B757299733DE24C3AAF74A1FBDA00277FF3EB48533B25E4C9D3"
    "selfie_segmentation.onnx"   = "3241AC4AD8AA35BDAF33946776DB29F7C283A413AA0B0DACB9483594B4531AAD"
}
$missing = @()
foreach ($name in $expected.Keys) {
    $path = Join-Path $models $name
    if (-not (Test-Path $path)) { $missing += $name; continue }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($hash -eq $expected[$name]) {
        Write-Host "verified $name"
    }
    else {
        Write-Warning "hash differs for $name (export toolchain differs?) expected $($expected[$name]) got $hash"
    }
}
if ($missing.Count -gt 0) {
    throw "Missing runtime models: $($missing -join ', ')"
}
Write-Host "All models are present. Runtime files:"
Get-ChildItem $models -Filter *.onnx | Select-Object Name, Length | Format-Table -AutoSize
