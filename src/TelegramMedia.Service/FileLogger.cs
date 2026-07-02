using System.Text;
using Microsoft.Extensions.Logging;

namespace TelegramMedia.Service;

/// <summary>
/// Minimal dependency-free file logger. The app is a WinExe with no console, so this makes
/// ILogger output visible for troubleshooting. Writes one file per day and never throws.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory)
    {
        _dir = directory;
        try { Directory.CreateDirectory(_dir); } catch { /* best-effort */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void Write(string line)
    {
        try
        {
            var path = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_lock) File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch { /* logging must never break the app */ }
    }

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Framework-configured filters (appsettings Logging:LogLevel) apply before this; we only
    // additionally drop Debug/Trace noise.
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var sb = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Level(logLevel)).Append("] ")
            .Append(_category).Append(": ")
            .Append(formatter(state, exception)).Append('\n');
        if (exception != null) sb.Append(exception).Append('\n');

        _provider.Write(sb.ToString());
    }

    private static string Level(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
