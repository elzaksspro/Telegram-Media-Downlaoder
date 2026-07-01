using TelegramMedia.Core.Enums;

namespace TelegramMedia.Telegram.Helpers;

public static class FileHelper
{
    public static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
            fileName = fileName.Replace(c, '_');
        return fileName.Trim();
    }

    public static MediaType GetMediaTypeFromMime(string? mimeType)
    {
        if (string.IsNullOrEmpty(mimeType)) return MediaType.File;

        if (mimeType.StartsWith("video/")) return MediaType.Video;
        if (mimeType.StartsWith("image/")) return MediaType.Photo;
        if (mimeType.StartsWith("audio/")) return MediaType.Music;
        return MediaType.File;
    }

    public static string BuildDownloadPath(string basePath, string template,
        string chatName, MediaType mediaType, string fileName, int messageId,
        string chatType = "Chat")
    {
        var sanitizedChat = SanitizeFileName(chatName);
        var sanitizedType = SanitizeFileName(chatType);
        var now = DateTime.Now;

        var relativePath = template
            .Replace("{chatType}", sanitizedType)
            .Replace("{chatName}", sanitizedChat)
            .Replace("{mediaType}", mediaType.ToString() + "s")
            .Replace("{year}", now.Year.ToString())
            .Replace("{month}", now.Month.ToString("D2"))
            .Replace("{day}", now.Day.ToString("D2"));

        var fullDir = Path.Combine(basePath, relativePath);
        Directory.CreateDirectory(fullDir);

        // Prefix with MessageId to prevent collisions when multiple messages share a filename.
        var safe = SanitizeFileName(fileName);
        var prefixed = $"{messageId}_{safe}";
        return Path.Combine(fullDir, prefixed);
    }
}
