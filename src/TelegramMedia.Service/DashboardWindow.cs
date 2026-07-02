using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TelegramMedia.Service;

/// <summary>
/// Native desktop window that hosts the Blazor dashboard via the Edge WebView2 runtime,
/// so the app feels like a real window instead of a browser tab. Falls back to the system
/// browser if the WebView2 runtime is unavailable. Closing hides the window (the tray keeps
/// the app running); it can be reopened from the tray.
/// </summary>
public sealed class DashboardWindow : Form
{
    private readonly WebView2 _web;
    private readonly Label _loading;
    private readonly string _url;
    private readonly string _userDataFolder;
    private bool _initFailed;

    public DashboardWindow(string url, string userDataFolder)
    {
        _url = url;
        _userDataFolder = userDataFolder;

        Text = "Telegram Media Downloader";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(37, 99, 235); // brand blue, avoids a white flash before load
        try { Icon = Program.LoadAppIcon(); } catch { /* icon is best-effort */ }

        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);

        // Branded "Starting…" splash shown until the dashboard finishes loading.
        _loading = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Starting Telegram Media Downloader…",
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 13F)
        };
        Controls.Add(_loading);
        _loading.BringToFront();

        _web.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                _loading.Visible = false;
            }
            else
            {
                // Server not ready yet — keep the splash and retry shortly.
                _loading.Text = "Starting Telegram Media Downloader…";
                _loading.Visible = true;
                _loading.BringToFront();
                _ = RetryNavigateAsync();
            }
        };

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, e) =>
        {
            // Hide instead of destroy so the tray can reopen it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.Source = new Uri(_url);
        }
        catch
        {
            // WebView2 runtime missing / failed — fall back to the default browser.
            _initFailed = true;
            OpenInBrowser();
            Hide();
        }
    }

    private async Task RetryNavigateAsync()
    {
        await Task.Delay(700);
        try { _web.CoreWebView2?.Navigate(_url); } catch { /* will retry on next failure */ }
    }

    public void ShowDashboard()
    {
        if (_initFailed)
        {
            OpenInBrowser();
            return;
        }
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        // Force the window to the foreground even when triggered from a background thread
        // (a second launch signalling this instance).
        var wasTopMost = TopMost;
        TopMost = true;
        TopMost = wasTopMost;
    }

    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _url, UseShellExecute = true });
        }
        catch { /* nothing more we can do */ }
    }
}
