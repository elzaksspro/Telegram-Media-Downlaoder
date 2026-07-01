namespace TelegramMedia.Core.Models;

public class AppSettings
{
    public int Id { get; set; } = 1; // Singleton row
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string DownloadPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "TelegramDownloads");
    public string PathTemplate { get; set; } = "{chatType}s/{chatName}/{mediaType}";
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int MaxSpeedKbps { get; set; } // 0 = unlimited
    public bool SendNotifications { get; set; }
    public string? NotificationBotToken { get; set; }
    public string? NotificationChatId { get; set; }
    public bool DarkMode { get; set; } = true;
    public int WebPort { get; set; } = 5000;
}
