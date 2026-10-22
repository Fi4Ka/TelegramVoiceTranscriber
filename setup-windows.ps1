# Run from PowerShell in the project folder: .\setup-windows.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$release = "v1.9.2"
$binUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/$release/whisper-bin-x64.zip"
$modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin"
$whisperDir = Join-Path $root "tools\whisper"
$modelDir = Join-Path $root "tools\models"
New-Item -ItemType Directory -Force -Path $whisperDir, $modelDir | Out-Null

if (-not (Test-Path (Join-Path $whisperDir "Release\whisper-cli.exe"))) {
    $archive = Join-Path $env:TEMP "whisper-bin-x64-$release.zip"
    Write-Host "Downloading whisper.cpp for Windows..."
    Invoke-WebRequest -Uri $binUrl -OutFile $archive
    Expand-Archive -Path $archive -DestinationPath $whisperDir -Force
    Remove-Item $archive -Force
}

$modelPath = Join-Path $modelDir "ggml-base.bin"
if (-not (Test-Path $modelPath)) {
    Write-Host "Downloading multilingual Whisper base model (~148 MB)..."
    Invoke-WebRequest -Uri $modelUrl -OutFile $modelPath
}

$exe = Join-Path $whisperDir "Release\whisper-cli.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Whisper was unpacked, but executable has a different directory layout."
    Get-ChildItem $whisperDir -Filter whisper-cli.exe -Recurse | Select-Object FullName
    throw "Update WhisperExe in settings.local.json to the path shown above."
}
Write-Host "OK: whisper.cpp and model are ready."
Write-Host "Next: winget install --id Gyan.FFmpeg -e"
Write-Host "Next: copy settings.example.json settings.local.json, set the BotToken, and dotnet run"
