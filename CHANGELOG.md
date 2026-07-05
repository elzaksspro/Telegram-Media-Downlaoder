# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-07-05

First public release. 🎉

### Highlights
- **Windows desktop app** — a single tray executable hosting a local Blazor dashboard in a
  native WebView2 window. No Windows service, no admin rights, per-user install.
- **Persistent Telegram login** — sign in once (phone → code → 2FA); the session resumes
  silently on every restart until Telegram itself revokes it.
- **IDM-style selective downloading** — scan a channel to *list* its media, then download
  individual files, a page, or everything. Per-file and bulk **Start / Pause / Cancel**,
  priority reordering (up/down/top/bottom), pagination, and select-all-matching-filter.
- **Granular pause** — global, per-channel (persists across restarts), and per-file;
  in-progress files finish, the rest hold.
- **Throughput control** — live concurrency limit and a global bandwidth cap (KB/s),
  enforced across all downloads.
- **Real-time monitoring** — auto-download new media from monitored chats as it arrives,
  with optional message reactions.
- **Dark mode** — toggle in the sidebar; follows the system preference by default.
- **Tray status** — Service (Running/Stopped) and Telegram (Connected / Sign-in required /
  Downloading / Paused) with a colored status dot and a Restart action; single-instance
  behavior focuses the running window instead of launching a duplicate.
- **Local-first** — settings, queue, history, and session are stored under
  `%LOCALAPPDATA%\TelegramMediaDownloader\`; downloads default to `Documents\TelegramDownloads`.
- Rolling log files under `%LOCALAPPDATA%\TelegramMediaDownloader\logs\` for troubleshooting.

[1.0.0]: https://github.com/elzaksspro/Telegram-Media-Downlaoder/releases/tag/v1.0.0
