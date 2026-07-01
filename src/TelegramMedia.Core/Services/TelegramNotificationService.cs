using System.Net.Http.Json;
using TelegramMedia.Core.Interfaces;

namespace TelegramMedia.Core.Services;

public class TelegramNotificationService : INotificationService
{
    private readonly HttpClient _http = new();
    private string? _botToken;
    private string? _chatId;

    public void Configure(string? botToken, string? chatId)
    {
        _botToken = botToken;
        _chatId = chatId;
    }

    public async Task SendAsync(string message)
    {
        if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId))
            return;

        try
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            await _http.PostAsJsonAsync(url, new { chat_id = _chatId, text = message });
        }
        catch
        {
            // Silent fail for notifications
        }
    }
}
