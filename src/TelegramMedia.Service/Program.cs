using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.EntityFrameworkCore;
using TelegramMedia.Core.Data;
using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Interfaces;
using TelegramMedia.Core.Services;
using TelegramMedia.Service;
using TelegramMedia.Telegram.Services;

namespace TelegramMedia.Service;

// Single user-mode desktop app: hosts the Blazor dashboard (Kestrel) in-process
// behind a system-tray icon. No Windows Service, so config/DB/session all live in
// the current user's profile and there is no UAC or cross-profile mismatch.
static class Program
{
    private const string MutexName = "TelegramMediaDownloaderApp";
    private const string ShowEventName = "TelegramMediaDownloaderShow";
    private static EventWaitHandle? _showEvent;

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegramMediaDownloader");
    private static readonly string PortConfigPath = Path.Combine(ConfigDir, "port.json");

    private static NotifyIcon? _trayIcon;
    private static ToolStripMenuItem? _portMenuItem;
    private static ToolStripMenuItem? _serviceMenuItem;
    private static ToolStripMenuItem? _statusMenuItem;
    private static DashboardWindow? _window;
    private static int _port;

    // Live status shown on the tray icon.
    private static ITelegramClientService? _telegram;
    private static IDownloadManager? _downloads;
    private static volatile bool _hostRunning;
    private static System.Windows.Forms.Timer? _statusTimer;
    private static readonly Dictionary<string, Icon> _statusIcons = new();
    private static string _lastStatusKey = "";

    [STAThread]
    static void Main(string[] args)
    {
        _port = ReadPortFromConfig();

        // Single instance. If another instance appears to be running, first wait for the
        // mutex — the previous instance may just be shutting down (it disconnects Telegram
        // before releasing, which can take a few seconds), in which case we take over.
        var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            try { isNew = mutex.WaitOne(TimeSpan.FromSeconds(12)); }
            catch (AbandonedMutexException) { isNew = true; } // previous instance was killed; we own it now
        }
        if (!isNew)
        {
            // A real instance is running — ask it to show/focus its window instead of opening
            // a browser at a port that may not be serving yet.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch
            {
                OpenBrowser();
            }
            return;
        }

        var app = BuildWebApp(args);

        // Resolve the singletons the tray uses to show live status.
        _telegram = app.Services.GetRequiredService<ITelegramClientService>();
        _downloads = app.Services.GetRequiredService<IDownloadManager>();

        // Track the in-process host (service) health for the tray status.
        app.Lifetime.ApplicationStarted.Register(() => _hostRunning = true);
        app.Lifetime.ApplicationStopping.Register(() => _hostRunning = false);
        app.Lifetime.ApplicationStopped.Register(() => _hostRunning = false);

        // Seed the database before the host starts handling requests.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EnsureCreatedAndSeedAsync().GetAwaiter().GetResult();
        }

        // Start Kestrel non-blocking, then run the WinForms message loop for the tray.
        app.StartAsync().GetAwaiter().GetResult();
        app.Logger.LogInformation("Telegram Media Downloader running on port {Port}", _port);

        try
        {
            ApplicationConfiguration.Initialize();
            RunTray();
            OpenDashboard(); // open the app window on launch
            StartActivationListener(); // let a second launch re-focus this window
            Application.Run();
        }
        finally
        {
            // Run shutdown OFF the UI thread. Blocking the STA main thread here (its WinForms
            // SynchronizationContext no longer pumps after Application.Run returns) can deadlock
            // and leave the process — and thus the single-instance mutex — alive, which stops
            // the app from launching again.
            try { Task.Run(() => app.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult(); }
            catch { /* ignore shutdown errors */ }

            // CRITICAL: fully disconnect the Telegram client BEFORE releasing the mutex.
            // If the old connection is still alive when the next instance connects with the
            // same auth key, Telegram raises AUTH_KEY_DUPLICATED and invalidates the saved
            // login — which is what forced users to re-authenticate after every restart.
            try { Task.Run(() => _telegram?.DisconnectAsync()).Wait(TimeSpan.FromSeconds(5)); }
            catch { /* best-effort */ }

            try { mutex.ReleaseMutex(); } catch { /* not owned */ }
            mutex.Dispose();
        }

        // Guarantee the process fully exits so the mutex is freed and the app can relaunch
        // (WebView2/host background threads can otherwise keep it alive).
        Environment.Exit(0);
    }

    private static WebApplication BuildWebApp(string[] args)
    {
        // Pin the content root to the exe directory so wwwroot resolves no matter where
        // the app is launched from (working dir is unpredictable for a tray/startup app,
        // and is System32 for a single-file build). This MUST be set at CreateBuilder time
        // — setting it afterwards leaves the already-built static-file provider on the
        // wrong root, which 404s the stylesheet.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = exeDir
        });

        // File logging (the app has no console) → %LOCALAPPDATA%\TelegramMediaDownloader\logs
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(ConfigDir, "logs")));

        // Dashboard binds to the user-configured port on localhost only.
        builder.WebHost.UseUrls($"http://localhost:{_port}");

        // Database (under the current user's local app data).
        var dbPath = Path.Combine(ConfigDir, "app.db");
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        // Core services
        builder.Services.AddSingleton<BandwidthLimiter>();
        builder.Services.AddSingleton<ITelegramClientService, TelegramClientService>();
        builder.Services.AddSingleton<IDownloadManager, DownloadManager>();
        builder.Services.AddSingleton<TelegramNotificationService>();
        builder.Services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<TelegramNotificationService>());

        // Background worker
        builder.Services.AddHostedService<TelegramMonitorWorker>();

        // Blazor Server
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();

        // Clean-exit hook (dashboard binds to localhost only). Same path as tray Exit —
        // used for scripted restarts and update flows.
        app.MapPost("/api/shutdown", () =>
        {
            _ = Task.Run(() => Application.Exit());
            return Results.Ok("shutting down");
        });

        return app;
    }

    // --- Tray UI ---

    private static void RunTray()
    {
        var menu = new ContextMenuStrip();

        // Live status headers (disabled, non-clickable): service (host) + Telegram.
        _serviceMenuItem = new ToolStripMenuItem("Service: starting…") { Enabled = false };
        _statusMenuItem = new ToolStripMenuItem("Telegram: …") { Enabled = false };
        menu.Items.Add(_serviceMenuItem);
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add("Open in Browser", null, (_, _) => OpenBrowser());
        menu.Items.Add("Open Downloads Folder", null, (_, _) => OpenDownloadsFolder());
        menu.Items.Add(new ToolStripSeparator());

        _portMenuItem = new ToolStripMenuItem($"Port: {_port}");
        _portMenuItem.Click += (_, _) => ChangePort();
        menu.Items.Add(_portMenuItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Restart", null, (_, _) => RestartApp());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _trayIcon = new NotifyIcon
        {
            Text = "Telegram Media Downloader",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        // Poll status and reflect it on the tray icon/tooltip/menu.
        UpdateTrayStatus();
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => UpdateTrayStatus();
        _statusTimer.Start();

        Application.ApplicationExit += (_, _) =>
        {
            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            foreach (var ic in _statusIcons.Values) ic.Dispose();
        };
    }

    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Amber = Color.FromArgb(217, 119, 6);
    private static readonly Color Gray = Color.FromArgb(107, 114, 128);
    private static readonly Color Red = Color.FromArgb(220, 38, 38);

    private static void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;

        // --- Service (in-process host) health ---
        var serviceRunning = _hostRunning;
        var serviceText = serviceRunning ? "Running" : "Stopped — use Restart";
        if (_serviceMenuItem is not null)
        {
            _serviceMenuItem.Text = $"Service: {serviceText}";
            _serviceMenuItem.ForeColor = serviceRunning ? Green : Red;
        }

        // --- Telegram connection status ---
        string tg;
        string key;   // icon dot: green | amber | gray | red
        Color color;

        var connected = _telegram is { IsConnected: true } && _telegram.AuthState == AuthState.Authenticated;
        var auth = _telegram?.AuthState ?? AuthState.NotAuthenticated;

        if (!connected)
        {
            if (auth is AuthState.WaitingForPhoneNumber or AuthState.WaitingForCode or AuthState.WaitingForPassword)
            {
                tg = "Sign-in required"; key = "amber"; color = Amber;
            }
            else
            {
                tg = "Connecting…"; key = "gray"; color = Gray;
            }
        }
        else if (_downloads is { IsPaused: true })
        {
            tg = "Paused"; key = "amber"; color = Amber;
        }
        else
        {
            var active = _downloads?.ActiveDownloads.Count(d => d.Status == DownloadStatus.Downloading) ?? 0;
            tg = active > 0 ? $"Downloading {active}" : "Connected";
            key = "green"; color = Green;
        }

        if (_statusMenuItem is not null)
        {
            _statusMenuItem.Text = $"Telegram: {tg}";
            _statusMenuItem.ForeColor = color;
        }

        // A stopped host dominates the icon/tooltip (it's the actionable problem).
        if (!serviceRunning) { key = "red"; color = Red; }

        // Tooltip (NotifyIcon.Text caps at 63 chars).
        var tip = $"Telegram Media Downloader — Service {serviceText.Split('—')[0].Trim()} · {tg}";
        if (tip.Length > 63) tip = tip[..63];
        if (_trayIcon.Text != tip) _trayIcon.Text = tip;

        // Swap the icon's status dot only when the state changes.
        if (key != _lastStatusKey)
        {
            _lastStatusKey = key;
            _trayIcon.Icon = GetStatusIcon(key, color);
        }
    }

    private static Icon GetStatusIcon(string key, Color color)
    {
        if (_statusIcons.TryGetValue(key, out var cached)) return cached;

        Icon icon;
        try
        {
            using var baseIcon = LoadAppIcon();
            using var bmp = baseIcon.ToBitmap();
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var d = Math.Max(6, bmp.Width * 7 / 16);
                var x = bmp.Width - d - 1;
                var y = bmp.Height - d - 1;
                using var fill = new SolidBrush(color);
                using var ring = new Pen(Color.White, Math.Max(1f, bmp.Width / 16f));
                g.FillEllipse(fill, x, y, d, d);
                g.DrawEllipse(ring, x, y, d, d);
            }
            icon = Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            icon = LoadAppIcon();
        }
        _statusIcons[key] = icon;
        return icon;
    }

    // The app icon is embedded in the exe via <ApplicationIcon>; pull it back out for
    // the tray and window so they match the taskbar icon.
    internal static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    // Listens for a "show" signal from a second launch and re-focuses the existing window,
    // so relaunching the app never opens a browser (which could hit a not-yet-ready port).
    private static void StartActivationListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { if (!_showEvent.WaitOne()) break; }
                catch { break; }
                ShowExistingWindow();
            }
        })
        {
            IsBackground = true,
            Name = "ActivationListener"
        };
        thread.Start();
    }

    private static void ShowExistingWindow()
    {
        var w = _window;
        if (w is { IsDisposed: false, IsHandleCreated: true })
        {
            try { w.BeginInvoke((Action)(() => w.ShowDashboard())); return; }
            catch { /* fall through to browser */ }
        }
        OpenBrowser();
    }

    private static void OpenDashboard()
    {
        try
        {
            if (_window is null || _window.IsDisposed)
            {
                var udf = Path.Combine(ConfigDir, "webview2");
                _window = new DashboardWindow($"http://localhost:{_port}", udf);
            }
            _window.ShowDashboard();
        }
        catch
        {
            OpenBrowser();
        }
    }

    private static void OpenBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{_port}",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private static void OpenDownloadsFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TelegramDownloads");
        Directory.CreateDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    private static void ChangePort()
    {
        using var dialog = new PortInputDialog(_port);
        if (dialog.ShowDialog() != DialogResult.OK || dialog.SelectedPort <= 0)
            return;

        SavePort(dialog.SelectedPort);

        var result = MessageBox.Show(
            $"Port changed to {dialog.SelectedPort}.\n\nRestart now for the change to take effect?",
            "Telegram Media Downloader",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
            RestartApp();
    }

    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--restarted",
                UseShellExecute = true
            });
        }
        Application.Exit();
    }

    // --- Config helpers ---

    private static int ReadPortFromConfig()
    {
        try
        {
            if (File.Exists(PortConfigPath))
            {
                var json = File.ReadAllText(PortConfigPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Port", out var portEl))
                {
                    var port = portEl.GetInt32();
                    if (port is >= 1024 and <= 65535) return port;
                }
            }
        }
        catch { }

        return 5000; // Default
    }

    private static void SavePort(int port)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(new { Port = port },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PortConfigPath, json);
        }
        catch { }
    }
}
