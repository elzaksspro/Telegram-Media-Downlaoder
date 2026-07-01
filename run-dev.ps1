# Development run script - starts the desktop app directly
# Run: powershell -ExecutionPolicy Bypass -File run-dev.ps1

Write-Host "Starting Telegram Media Downloader (dev mode)..." -ForegroundColor Cyan
Write-Host "Dashboard port is read from %LOCALAPPDATA%\TelegramMediaDownloader\port.json (default 5000)." -ForegroundColor Green
Write-Host "A tray icon appears; double-click it to open the dashboard. Exit via the tray menu.`n" -ForegroundColor Yellow

dotnet run --project src/TelegramMedia.Service/TelegramMedia.Service.csproj
