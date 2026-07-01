namespace TelegramMedia.Core.Enums;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Skipped,
    Available // Listed from a scan but not yet queued for download
}
