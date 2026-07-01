using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Models;

namespace TelegramMedia.Core.Interfaces;

public interface ITelegramClientService
{
    AuthState AuthState { get; }
    bool IsConnected { get; }
    event Action<DownloadProgress>? OnDownloadProgress;
    event Action<string>? OnStatusMessage;

    Task ConnectAsync(int apiId, string apiHash);
    Task<string> SendCodeAsync(string phoneNumber);
    Task<bool> SubmitCodeAsync(string code);
    Task<bool> SubmitPasswordAsync(string password);
    Task DisconnectAsync();

    Task<List<MonitoredChat>> GetAllChatsAsync();
    Task StartMonitoringAsync(CancellationToken cancellationToken);
    Task StopMonitoringAsync();

    Task DownloadFileAsync(DownloadTask task, string basePath, string pathTemplate,
        CancellationToken cancellationToken, IProgress<DownloadProgress>? progress = null);

    Task<(int scanned, int queued)> ScanAndEnqueueHistoryAsync(
        long chatId, CancellationToken cancellationToken);

    /// <summary>
    /// Scan chat history and record matching media as Available tasks WITHOUT downloading.
    /// The user then selects which to download. Returns (scanned messages, newly listed).
    /// </summary>
    Task<(int scanned, int listed)> ScanAndListHistoryAsync(
        long chatId, CancellationToken cancellationToken);
}
