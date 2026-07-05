<div align="center">

<img src="src/TelegramMedia.Service/wwwroot/logo.svg" width="104" alt="Telegram Media Downloader logo" />

# Telegram Media Downloader

**A Windows desktop app that auto-downloads media from your Telegram chats and channels — with a full, IDM-style download manager.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![UI](https://img.shields.io/badge/UI-Blazor%20%2B%20WebView2-2563eb)

</div>

---

## Overview

Telegram Media Downloader runs as a single **system-tray desktop app**. It hosts a local
dashboard (Blazor) that opens in a **native window** (Edge WebView2) — no browser tab, no
Windows service, and no admin rights. Point it at your Telegram channels and it works like a
proper download manager: **scan a channel, pick exactly what you want, and control the queue**.

> You need your own Telegram **API ID** and **API Hash** (free) from
> <https://my.telegram.org/apps>. The app signs in as *you* via Telegram's official API.

## Features

- **🖥️ Real desktop app** — one tray executable; the dashboard opens in a native WebView2
  window. No service, no UAC prompts, everything runs in your user session.
- **🎯 Selective downloading** — *scan* a channel to **list** its media (nothing downloads),
  then choose **individual files**, a **page**, or **everything**.
- **⏯️ Download-manager controls** — per-file and bulk **Start / Pause / Cancel**, **priority**
  (up/down, to top/bottom), **pagination** for huge queues, and **select-all-matching-filter**.
- **⏸️ Granular pause** — pause **globally**, **per-channel**, or **per-file**; in-progress
  files finish, the rest hold. Per-channel pause survives restarts.
- **🚦 Throughput limits** — live **concurrency** control and a global **bandwidth cap** (KB/s),
  enforced across every download.
- **📡 Real-time monitoring** — auto-download new media from monitored chats as it arrives,
  with optional message **reactions**.
- **🔎 Filtering** — by filename, media type, and status; per-channel media-type and
  min-size rules.
- **💾 Persistent** — settings, queue, history, and pause state are stored locally (SQLite)
  and survive restarts.
- **🌙 Dark mode** — sidebar toggle; follows your system preference by default.

## Download & install (end users)

1. Go to the [**Releases**](../../releases) page.
2. Download **`TelegramMediaDownloader-Setup-x.y.z.exe`** (the installer, ~57 MB).
3. Double-click it. Because the app isn't code-signed, Windows **SmartScreen** may warn —
   click **More info → Run anyway**.
4. It installs **per-user** (no admin), adds a Start-menu shortcut, sets it to **launch at
   login**, and opens the app window.
5. On first run, connect your Telegram account (see [First run](#first-run)).

> Prefer not to install? A **portable** single-file `TelegramMedia.Service.exe` may also be
> attached to the release — just download and run it (no install, no shortcuts). It's larger
> (~189 MB) because it bundles the .NET runtime.

**Requirements:** Windows 10/11 and the
[Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
(preinstalled on Windows 11; the app falls back to your default browser if it's missing).

## First run

1. Launch the app — the dashboard window opens automatically.
2. Enter your **API ID**, **API Hash**, and **phone number** (with country code, e.g.
   `+2348012345678`), then click **Send Verification Code**.
3. Enter the code Telegram sends you (and your **2FA password** if you have one).
4. Once connected, go to **Chats**, tick the channels/groups to monitor, then open one.

## Using it

- **Scan Channel** — lists the channel's media as *Available* (nothing downloads yet).
- Tick the files you want (or **Select all N matching** the current filter), then **▶ Download**.
  Or hit **Download All**.
- Manage the queue with per-row **▶ / ⏸ / ✖** and priority **⤒ ▲ ▼ ⤓**, or the bulk bar.
- **Pause Chat** holds just that channel; the global **⏸ Pause** on the Downloads page holds
  everything.
- Set **Concurrent downloads** and a **Speed limit (KB/s)** on the Downloads page or in
  **Settings** — applied live.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download). To also build the installer,
install [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
# Run from source (dev) — launches the tray app + dashboard
powershell -ExecutionPolicy Bypass -File run-dev.ps1

# Produce the self-contained exe (+ installer if Inno Setup is present)
powershell -ExecutionPolicy Bypass -File build.ps1
```

Outputs:

| Artifact | Path | Use |
| --- | --- | --- |
| Installer | `output/TelegramMediaDownloader-Setup-<ver>.exe` | **Distribute this** to end users |
| Portable exe | `publish/app/TelegramMedia.Service.exe` | Optional no-install download |

## Code signing (optional)

The app and installer are unsigned by default, so Windows SmartScreen warns on download. To
sign them, `build.ps1` runs `signtool` automatically when a certificate is configured — set
**one** of these before building:

```powershell
# Option 1: a .pfx file
$env:SIGN_PFX = "C:\path\to\cert.pfx"; $env:SIGN_PFX_PASSWORD = "…"

# Option 2: a cert already installed in your certificate store
$env:SIGN_THUMBPRINT = "<thumbprint>"

powershell -ExecutionPolicy Bypass -File build.ps1
```

Certificate options:

- **[Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)** (~$10/mo) —
  the cheapest way to get a Microsoft-trusted signature; recommended for individuals.
- **OV code-signing certificate** from a CA (~$100–400/yr) — SmartScreen reputation builds up
  over downloads; **EV** certificates are trusted immediately but cost more.
- **Self-signed** — only useful for local testing; it does **not** satisfy SmartScreen.

Requires the [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/)
(for `signtool.exe`). Without a cert configured, signing is silently skipped.

## Project layout

| Project | Purpose |
| --- | --- |
| `TelegramMedia.Core` | Models, EF Core (SQLite) data layer, shared services (bandwidth limiter) |
| `TelegramMedia.Telegram` | Telegram client ([WTelegramClient](https://github.com/wiz0u/WTelegramClient)) + the download manager / priority dispatcher |
| `TelegramMedia.Service` | Blazor dashboard, tray host, and WebView2 window — the app entry point |

## Where your data lives

- Config, database, and Telegram session: `%LOCALAPPDATA%\TelegramMediaDownloader\`
- Downloads (default): `Documents\TelegramDownloads` — configurable in **Settings**
- The app binds its dashboard to `http://localhost:<port>` (default `5000`, changeable from
  the tray menu; stored in `port.json`).

## Troubleshooting

- **"Not connected to Telegram"** on the Chats/channel pages — finish sign-in on the
  Dashboard (phone → code). Scanning and downloading require an active session.
- **A download shows "Failed"** — hover the status for the exact error (e.g. rate limits,
  file removed). Re-select the file and hit ▶ to retry.
- **Window is blank / won't open** — install the
  [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/); the app
  otherwise falls back to your default browser (tray → *Open in Browser*).
- **SmartScreen blocks the installer** — the build isn't code-signed; choose
  **More info → Run anyway**.

## License

Released under the [MIT License](LICENSE). See [CHANGELOG.md](CHANGELOG.md) for version history.

## Disclaimer

Use this tool only to download content you have the right to access, and in accordance with
Telegram's Terms of Service.
