using TelegramMedia.Core.Enums;

namespace TelegramMedia.Core.Models;

public class DownloadProgress
{
    public int TaskId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ChatName { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
    public DownloadStatus Status { get; set; }
    public long FileSize { get; set; }
    public long DownloadedBytes { get; set; }
    public double SpeedKbps { get; set; }
    public double ProgressPercent => FileSize > 0 ? (double)DownloadedBytes / FileSize * 100 : 0;
    public string? ErrorMessage { get; set; }

    public string SpeedDisplay
    {
        get
        {
            if (SpeedKbps <= 0) return "--";
            if (SpeedKbps >= 1024) return $"{SpeedKbps / 1024:F1} MB/s";
            return $"{SpeedKbps:F0} KB/s";
        }
    }

    public string SizeDisplay
    {
        get
        {
            if (FileSize <= 0) return "Unknown";
            if (FileSize >= 1024 * 1024 * 1024) return $"{FileSize / (1024.0 * 1024 * 1024):F1} GB";
            if (FileSize >= 1024 * 1024) return $"{FileSize / (1024.0 * 1024):F1} MB";
            return $"{FileSize / 1024.0:F0} KB";
        }
    }
}
