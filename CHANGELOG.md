# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-07-02

### Fixed
- **Telegram login now persists across restarts.** A transient network error at
  startup (the monitor connects the moment the app launches) was being treated as an
  invalid session, forcing re-authentication every time. The app now distinguishes a
  genuinely expired session from a transient failure: it keeps the stored session and
  retries silently, and only prompts to sign in when the session is actually gone.
- The real reason for a failed session resume is now logged instead of being swallowed.

### Added
- A rolling log file at `%LOCALAPPDATA%\TelegramMediaDownloader\logs\app-<date>.log`
  for troubleshooting (the app has no console window).
- Optional code-signing support in `build.ps1` (set a certificate via environment
  variables to sign the app and installer).

## [1.1.0] - 2026-07-01

### Added
- Initial public release as a **single desktop app**: one system-tray executable that
  hosts the dashboard in a native Edge WebView2 window (no Windows service, no admin).
- **IDM-style selective downloading** — scan a channel to *list* its media, then download
  individual files, a page, or everything.
- Per-file and bulk **Start / Pause / Cancel**, **priority** ordering (up/down, to
  top/bottom), and **pagination** for large queues, with select-all-matching-filter.
- **Global, per-channel, and per-file pause**; per-channel pause persists across restarts.
- Live **concurrency limit** and global **bandwidth cap** (KB/s).
- **Real-time monitoring** of chosen chats with optional reactions.
- App **logo/branding** across the taskbar, tray, window, and browser favicon.

[1.1.1]: https://github.com/elzaksspro/Telegram-Media-Downlaoder/releases/tag/v1.1.1
[1.1.0]: https://github.com/elzaksspro/Telegram-Media-Downlaoder/releases/tag/v1.1.0
