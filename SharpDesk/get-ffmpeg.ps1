# Downloads FFmpeg 8.x shared DLLs needed for H.264 hardware encoding in SharpDesk.
# Run from the SharpDesk project folder: .\get-ffmpeg.ps1

$ErrorActionPreference = "Stop"
$outDir = Join-Path $PSScriptRoot "bin\Debug\net9.0\win-x64"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip"

if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$tmp = Join-Path $env:TEMP "ffmpeg-dl.zip"
Write-Host "Downloading FFmpeg shared build..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing

$extract = Join-Path $env:TEMP "ffmpeg-extract"
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Write-Host "Extracting..."
Expand-Archive -Path $tmp -DestinationPath $extract

$binDir = Get-ChildItem -Path $extract -Recurse -Directory -Filter "bin" | Select-Object -First 1
$dlls = Get-ChildItem -Path $binDir.FullName -Filter "*.dll"
foreach ($dll in $dlls) {
    Copy-Item $dll.FullName -Destination $outDir -Force
    Write-Host "  Copied $($dll.Name)" -ForegroundColor Green
}

Remove-Item $tmp -Force
Remove-Item $extract -Recurse -Force
Write-Host "`nDone! $($dlls.Count) DLLs copied to $outDir" -ForegroundColor Cyan
Write-Host "H.264 hardware encoding is now available." -ForegroundColor Green
