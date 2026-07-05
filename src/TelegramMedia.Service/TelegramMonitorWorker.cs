using Microsoft.EntityFrameworkCore;
using TelegramMedia.Core.Data;
using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Interfaces;

namespace TelegramMedia.Service;

public class TelegramMonitorWorker : BackgroundService
{
    private readonly ITelegramClientService _telegramClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramMonitorWorker> _logger;
    private AuthState? _lastLoggedWaitState;

    public TelegramMonitorWorker(
        ITelegramClientService telegramClient,
        IServiceProvider serviceProvider,
        ILogger<TelegramMonitorWorker> logger)
    {
        _telegramClient = telegramClient;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram Monitor Worker starting...");

        // Wait for settings to be configured
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var settings = await db.Settings.FirstOrDefaultAsync(stoppingToken);

                if (settings == null || settings.ApiId == 0 || string.IsNullOrEmpty(settings.ApiHash))
                {
                    _logger.LogInformation("Waiting for API credentials to be configured...");
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                // Only initiate connect from the worker when no auth flow is in progress.
                // If the user is mid-login (WaitingForCode/Password), the UI owns the client
                // — touching it here would dispose the active flow and cause a NullRef.
                if (!_telegramClient.IsConnected
                    && _telegramClient.AuthState == AuthState.NotAuthenticated)
                {
                    await _telegramClient.ConnectAsync(settings.ApiId, settings.ApiHash);
                }

                if (_telegramClient.AuthState != AuthState.Authenticated)
                {
                    // Log only when the state changes, so a signed-out app doesn't spam the log
                    // every 5s while it waits for the user to sign in.
                    if (_lastLoggedWaitState != _telegramClient.AuthState)
                    {
                        _lastLoggedWaitState = _telegramClient.AuthState;
                        _logger.LogInformation("Waiting for authentication... (State: {State})", _telegramClient.AuthState);
                    }
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }
                _lastLoggedWaitState = null;

                // Start monitoring
                _logger.LogInformation("Starting real-time monitoring...");
                await _telegramClient.StartMonitoringAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitor worker error, restarting in 10s...");
                await Task.Delay(10000, stoppingToken);
            }
        }

        _logger.LogInformation("Telegram Monitor Worker stopped.");
    }
}
