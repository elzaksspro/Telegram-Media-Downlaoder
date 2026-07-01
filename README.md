# Telegram Media Downloader

A Windows desktop app that auto-downloads media from your Telegram chats and channels.
It runs as a single tray application, hosts a local Blazor dashboard (opened in a native
WebView2 window), and works like a proper download manager — scan a channel, pick exactly
what you want, and control the queue.

## Features

- **Desktop app** — single tray executable; the dashboard opens in a native window (WebView2),
  no separate service and no admin rights required.
- **Selective downloading** — scan a channel to *list* its media, then select individual
  files, a page, or everything, and download only what you choose.
- **Download-manager controls** — per-file and bulk **Start / Pause / Cancel**, **priority**
  (move up/down, to top/bottom), global and **per-channel pause**, and **pagination** for
  large queues.
- **Throughput control** — live **concurrency limit** and a global **bandwidth cap** (KB/s).
- **Real-time monitoring** — auto-download new media from monitored chats as it arrives,
  with optional reactions.
- **Persistent** — settings, queue, and per-chat pause state are stored locally (SQLite) and
  survive restarts.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (to build)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
  (preinstalled on Windows 11; the app falls back to the default browser if missing)
- Telegram API credentials (API ID + API Hash) from <https://my.telegram.org/apps>

## Build & run

```powershell
# Run from source (dev)
powershell -ExecutionPolicy Bypass -File run-dev.ps1

# Produce a self-contained exe + installer (installer needs Inno Setup 6)
powershell -ExecutionPolicy Bypass -File build.ps1
```

The published app lands in `publish/app/TelegramMedia.Service.exe`, and the installer (if
Inno Setup is installed) in `output/`.

## First run

1. Launch the app — the dashboard window opens automatically.
2. Enter your **API ID**, **API Hash**, and **phone number**, then verify the code Telegram
   sends you (and your 2FA password if enabled).
3. Go to **Chats**, tick the channels to monitor, open one, **Scan Channel**, then select and
   download what you want.

## Project layout

| Project | Purpose |
| --- | --- |
| `TelegramMedia.Core` | Models, EF Core data layer, shared services (e.g. bandwidth limiter) |
| `TelegramMedia.Telegram` | Telegram client (WTelegramClient) + download manager/dispatcher |
| `TelegramMedia.Service` | Blazor dashboard + tray host + WebView2 window (the app entry point) |

## Data location

Config, database, and Telegram session live under
`%LOCALAPPDATA%\TelegramMediaDownloader\`. Downloads default to
`Documents\TelegramDownloads` (configurable in Settings).
