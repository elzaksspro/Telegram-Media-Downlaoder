using TelegramMedia.Core.Enums;

namespace TelegramMedia.Core.Models;

public class DownloadTask
{
    public int Id { get; set; }
    public long TelegramChatId { get; set; }
    public string ChatName { get; set; } = string.Empty;
    public int MessageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long DownloadedBytes { get; set; }
    public MediaType MediaType { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public int Priority { get; set; } // Lower downloads first (0 = default)
    public string? ErrorMessage { get; set; }
    public string? FilePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
