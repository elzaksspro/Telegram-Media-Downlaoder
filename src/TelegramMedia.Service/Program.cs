using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.EntityFrameworkCore;
using TelegramMedia.Core.Data;
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

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelegramMediaDownloader");
    private static readonly string PortConfigPath = Path.Combine(ConfigDir, "port.json");

    private static NotifyIcon? _trayIcon;
    private static ToolStripMenuItem? _portMenuItem;
    private static DashboardWindow? _window;
    private static int _port;

    [STAThread]
    static void Main(string[] args)
    {
        _port = ReadPortFromConfig();

        // Single instance. When relaunching for a port change ("--restarted"),
        // wait briefly for the previous instance to release the mutex.
        var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew && args.Contains("--restarted"))
            isNew = mutex.WaitOne(TimeSpan.FromSeconds(5));
        if (!isNew)
        {
            OpenBrowser();
            return;
        }

        var app = BuildWebApp(args);

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
            Application.Run();
        }
        finally
        {
            app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            mutex.Dispose();
        }
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

        return app;
    }

    // --- Tray UI ---

    private static void RunTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add("Open in Browser", null, (_, _) => OpenBrowser());
        menu.Items.Add("Open Downloads Folder", null, (_, _) => OpenDownloadsFolder());
        menu.Items.Add(new ToolStripSeparator());

        _portMenuItem = new ToolStripMenuItem($"Port: {_port}");
        _portMenuItem.Click += (_, _) => ChangePort();
        menu.Items.Add(_portMenuItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());

        _trayIcon = new NotifyIcon
        {
            Text = $"Telegram Media Downloader (:{_port})",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        _trayIcon.ShowBalloonTip(3000, "Telegram Media Downloader",
            $"Running on port {_port}. Double-click to open dashboard.",
            ToolTipIcon.Info);

        Application.ApplicationExit += (_, _) =>
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        };
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
