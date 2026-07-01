namespace TelegramMedia.Core.Interfaces;

public interface INotificationService
{
    Task SendAsync(string message);
}
