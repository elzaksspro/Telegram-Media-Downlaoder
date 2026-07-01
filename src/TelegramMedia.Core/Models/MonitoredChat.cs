using TelegramMedia.Core.Enums;

namespace TelegramMedia.Core.Models;

public class MonitoredChat
{
    public int Id { get; set; }
    public long TelegramChatId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string ChatType { get; set; } = "Group"; // Group, Channel, User
    public bool IsMonitored { get; set; }
    public bool IsPaused { get; set; } // Downloads for this chat are individually paused
    public string ReactionIcon { get; set; } = string.Empty;

    // Download policy
    public bool DownloadVideos { get; set; } = true;
    public bool DownloadPhotos { get; set; } = true;
    public bool DownloadMusic { get; set; }
    public bool DownloadFiles { get; set; }
    public int MinFileSizeMb { get; set; }
    public int MaxFileSizeMb { get; set; }
    public string? IgnorePatterns { get; set; } // JSON array of regex patterns

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
