# Build script for Telegram Media Downloader
# Run: powershell -ExecutionPolicy Bypass -File build.ps1
#
# Optional code signing (removes the Windows SmartScreen warning once your cert has
# reputation). Configure ONE of the following before running, then the app + installer
# are signed automatically:
#   $env:SIGN_PFX = "C:\path\to\cert.pfx"; $env:SIGN_PFX_PASSWORD = "…"
#   -- or --
#   $env:SIGN_THUMBPRINT = "<cert thumbprint installed in your certificate store>"

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $found = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

function Sign-File([string]$path) {
    $havePfx = $env:SIGN_PFX -and (Test-Path $env:SIGN_PFX)
    $haveThumb = [bool]$env:SIGN_THUMBPRINT
    if (-not ($havePfx -or $haveThumb)) {
        Write-Host "  (signing skipped for $(Split-Path $path -Leaf) - no cert configured)" -ForegroundColor DarkGray
        return
    }
    $signtool = Find-SignTool
    if (-not $signtool) {
        Write-Host "  signtool.exe not found (install the Windows SDK) - skipping signing." -ForegroundColor Yellow
        return
    }
    $common = @('/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
    if ($havePfx) {
        & $signtool sign @common /f $env:SIGN_PFX /p $env:SIGN_PFX_PASSWORD $path
    } else {
        & $signtool sign @common /sha1 $env:SIGN_THUMBPRINT $path
    }
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $path" }
    Write-Host "  signed $(Split-Path $path -Leaf)" -ForegroundColor Green
}

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

# Sign the app exe before it is packaged into the installer.
Sign-File "publish/app/TelegramMedia.Service.exe"

# Build installer (if Inno Setup is installed)
Write-Host "`n[2/2] Building Installer..." -ForegroundColor Yellow
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) {
    & $iscc installer/setup.iss
    Write-Host "`nInstaller created in output/" -ForegroundColor Green
    # Sign the installer itself.
    $setup = Get-ChildItem "output/TelegramMediaDownloader-Setup-*.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($setup) { Sign-File $setup.FullName }
} else {
    Write-Host "Inno Setup not found at $iscc - skipping installer build." -ForegroundColor Yellow
    Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "App: publish/app/TelegramMedia.Service.exe"
