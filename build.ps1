# Build script for Telegram Media Downloader
# Run: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = "Stop"

Write-Host "=== Building Telegram Media Downloader ===" -ForegroundColor Cyan

# Clean previous builds
if (Test-Path "publish") { Remove-Item -Recurse -Force "publish" }
if (Test-Path "output") { Remove-Item -Recurse -Force "output" }

# Publish the single desktop app (self-contained, hosts the Blazor dashboard + tray)
Write-Host "`n[1/2] Publishing App..." -ForegroundColor Yellow
dotnet publish src/TelegramMedia.Service/TelegramMedia.Service.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o publish/app

# Build installer (if Inno Setup is installed)
Write-Host "`n[2/2] Building Installer..." -ForegroundColor Yellow
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) {
    & $iscc installer/setup.iss
    Write-Host "`nInstaller created in output/" -ForegroundColor Green
} else {
    Write-Host "Inno Setup not found at $iscc - skipping installer build." -ForegroundColor Yellow
    Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "App: publish/app/TelegramMedia.Service.exe"
