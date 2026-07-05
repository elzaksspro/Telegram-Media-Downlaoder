using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;
using TelegramMedia.Core.Data;
using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Interfaces;
using TelegramMedia.Core.Models;
using TelegramMedia.Core.Services;
using TelegramMedia.Telegram.Helpers;

namespace TelegramMedia.Telegram.Services;

public class TelegramClientService : ITelegramClientService, IDisposable
{
    private readonly ILogger<TelegramClientService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private Client? _client;
    private User? _currentUser;
    private readonly string _sessionPath;

    // Auth flow state
    private TaskCompletionSource<string>? _codeTcs;
    private TaskCompletionSource<string>? _passwordTcs;
    private int _apiId;
    private string _apiHash = string.Empty;

    public AuthState AuthState { get; private set; } = AuthState.NotAuthenticated;
    public bool IsConnected => _client != null && _currentUser != null;

    public event Action<DownloadProgress>? OnDownloadProgress;
    public event Action<string>? OnStatusMessage;

    public TelegramClientService(
        ILogger<TelegramClientService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegramMediaDownloader", "session.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);

        WireWTelegramLogging();
    }

    private static bool _wtLogWired;
    private void WireWTelegramLogging()
    {
        if (_wtLogWired) return;
        _wtLogWired = true;
        WTelegram.Helpers.Log = (lvl, msg) =>
        {
            // Only surface warnings/errors to keep logs small during heavy downloading; routine
            // WTelegram chatter (levels 0-2) is dropped.
            if (lvl < 3) return;
            var level = lvl == 4 ? LogLevel.Error : lvl >= 5 ? LogLevel.Critical : LogLevel.Warning;
            _logger.Log(level, "[WTelegram] {Message}", msg);
        };
    }

    private Client CreateClient(string? phoneNumber = null)
    {
        string? ConfigProvider(string what)
        {
            switch (what)
            {
                case "api_id": return _apiId.ToString();
                case "api_hash": return _apiHash;
                // Let WTelegram own the session file (open/write/flush/truncate). This is the
                // supported way to persist a login; passing a raw FileStream made the app
                // responsible for those semantics and dropped the saved authorization.
                case "session_pathname": return _sessionPath;
                case "phone_number":
                    var phone = phoneNumber ?? _pendingPhone;
                    // If WTelegram asks for a phone number while we're only trying to resume a
                    // stored session, the session is gone/expired → a real login is required.
                    if (string.IsNullOrEmpty(phone)) _resumeNeededLogin = true;
                    return phone;
                case "verification_code": return _codeTcs?.Task.GetAwaiter().GetResult();
                case "password": return _passwordTcs?.Task.GetAwaiter().GetResult();
                default: return null;
            }
        }

        Client Build()
        {
            var c = new Client(ConfigProvider);
            c.PingInterval = 60;
            return c;
        }

        try
        {
            return Build();
        }
        catch (Exception ex)
        {
            // WTelegram's SessionStore couldn't read the session file — either an incompatible
            // file from an older build (raw-stream format) or a corrupted session. The failed
            // constructor leaks the file handle, so force it closed, move the bad file aside,
            // and start fresh. The user signs in once more; it then persists normally.
            _logger.LogWarning(ex, "Session file unreadable; resetting {Path}", _sessionPath);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (File.Exists(_sessionPath))
                    File.Move(_sessionPath, _sessionPath + ".old", overwrite: true);
            }
            catch
            {
                try { File.Delete(_sessionPath); } catch { /* best-effort */ }
            }
            return Build();
        }
    }

    private string? _pendingPhone;
    private bool _resumeNeededLogin;

    public async Task ConnectAsync(int apiId, string apiHash)
    {
        if (_client != null && _currentUser != null) return;

        _apiId = apiId;
        _apiHash = apiHash;

        _client?.Dispose();
        _codeTcs = new TaskCompletionSource<string>();
        _passwordTcs = new TaskCompletionSource<string>();
        _resumeNeededLogin = false;
        _client = CreateClient();

        // Try to resume the stored session (no phone number provided). reloginOnFailedResume:false
        // so a failed resume throws the REAL error instead of silently starting a fresh login
        // (which would ask for a phone number and hide why the session didn't resume).
        try
        {
            _currentUser = await _client.LoginUserIfNeeded(null, reloginOnFailedResume: false);
            AuthState = AuthState.Authenticated;
            OnStatusMessage?.Invoke($"Logged in as {_currentUser.first_name} {_currentUser.last_name}".Trim());
            _logger.LogInformation("Authenticated as {User} (resumed session)", _currentUser.first_name);
        }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;

            if (_resumeNeededLogin)
            {
                // The stored session is genuinely gone/expired → user must sign in again.
                AuthState = AuthState.WaitingForPhoneNumber;
                _logger.LogInformation(ex, "No valid Telegram session; interactive login required.");
            }
            else
            {
                // Transient failure (e.g. no network yet). KEEP the session file and stay
                // "NotAuthenticated" so the monitor worker retries the resume — do NOT force
                // the user to log in again.
                AuthState = AuthState.NotAuthenticated;
                OnStatusMessage?.Invoke("Could not reach Telegram; will retry (still signed in).");
                _logger.LogWarning(ex, "Telegram session resume failed transiently; will retry.");
            }
        }
    }

    public async Task<string> SendCodeAsync(string phoneNumber)
    {
        _pendingPhone = phoneNumber;
        _codeTcs = new TaskCompletionSource<string>();
        _passwordTcs = new TaskCompletionSource<string>();

        _client?.Dispose();
        _client = CreateClient(phoneNumber);

        AuthState = AuthState.WaitingForCode;
        OnStatusMessage?.Invoke($"Sending verification code to {phoneNumber}...");

        // Run login flow in background — LoginUserIfNeeded will block on
        // _codeTcs and _passwordTcs via the ConfigProvider callback
        _ = Task.Run(async () =>
        {
            try
            {
                _currentUser = await _client.LoginUserIfNeeded();
                AuthState = AuthState.Authenticated;
                OnStatusMessage?.Invoke($"Logged in as {_currentUser.first_name} {_currentUser.last_name}".Trim());
                _logger.LogInformation("Authenticated as {User}; Telegram session saved to {Path}",
                    _currentUser.first_name, _sessionPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed");
                OnStatusMessage?.Invoke($"Login failed: {ex.Message}");
                AuthState = AuthState.NotAuthenticated;
            }
        });

        await Task.Delay(2000);
        return "Verification code sent";
    }

    public Task<bool> SubmitCodeAsync(string code)
    {
        if (_codeTcs != null && !_codeTcs.Task.IsCompleted)
        {
            _codeTcs.SetResult(code);
            AuthState = AuthState.WaitingForPassword; // May or may not need password
        }
        return Task.FromResult(true);
    }

    public Task<bool> SubmitPasswordAsync(string password)
    {
        if (_passwordTcs != null && !_passwordTcs.Task.IsCompleted)
        {
            _passwordTcs.SetResult(password);
        }
        return Task.FromResult(true);
    }

    public async Task<List<MonitoredChat>> GetAllChatsAsync()
    {
        if (_client == null) throw new InvalidOperationException("Client not connected");

        var dialogs = await _client.Messages_GetAllDialogs();
        var result = new List<MonitoredChat>();

        // Process groups and channels
        foreach (var (id, chatBase) in dialogs.chats)
        {
            if (!chatBase.IsActive) continue;
            result.Add(new MonitoredChat
            {
                TelegramChatId = chatBase.ID,
                Name = chatBase.Title ?? $"Chat {id}",
                Username = chatBase.MainUsername,
                ChatType = chatBase.IsGroup ? "Group" : "Channel"
            });
        }

        // Process users
        foreach (var (id, user) in dialogs.users)
        {
            result.Add(new MonitoredChat
            {
                TelegramChatId = user.ID,
                Name = $"{user.first_name} {user.last_name}".Trim(),
                Username = user.MainUsername,
                ChatType = "User"
            });
        }

        return result.OrderBy(c => c.ChatType).ThenBy(c => c.Name).ToList();
    }

    public Task<(int scanned, int queued)> ScanAndEnqueueHistoryAsync(
        long chatId, CancellationToken cancellationToken)
        => ScanHistoryCoreAsync(chatId, enqueue: true, cancellationToken);

    public Task<(int scanned, int listed)> ScanAndListHistoryAsync(
        long chatId, CancellationToken cancellationToken)
        => ScanHistoryCoreAsync(chatId, enqueue: false, cancellationToken);

    private async Task<(int scanned, int added)> ScanHistoryCoreAsync(
        long chatId, bool enqueue, CancellationToken cancellationToken)
    {
        if (_client == null) throw new InvalidOperationException("Client not connected");

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var downloadManager = scope.ServiceProvider.GetRequiredService<IDownloadManager>();

        var monitored = await db.MonitoredChats
            .FirstOrDefaultAsync(c => c.TelegramChatId == chatId, cancellationToken)
            ?? throw new InvalidOperationException($"Chat {chatId} not found");

        var dialogs = await _client.Messages_GetAllDialogs();
        InputPeer? inputPeer = null;
        foreach (var (_, cb) in dialogs.chats)
            if (cb.ID == chatId) { inputPeer = cb.ToInputPeer(); break; }
        if (inputPeer == null)
            foreach (var (_, u) in dialogs.users)
                if (u.ID == chatId) { inputPeer = u.ToInputPeer(); break; }
        if (inputPeer == null)
            throw new InvalidOperationException($"Could not resolve chat {chatId}");

        var verb = enqueue ? "queued" : "listed";
        int scanned = 0, added = 0;
        int offsetId = 0;
        const int batchSize = 100;

        OnStatusMessage?.Invoke($"Scanning history of {monitored.Name}...");

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _client.Messages_GetHistory(inputPeer, offsetId, default, 0, batchSize, 0, 0);
            var msgs = batch.Messages.OfType<Message>().ToList();
            if (msgs.Count == 0) break;

            foreach (var msg in msgs)
            {
                scanned++;
                var (mediaType, fileName, fileSize) = GetMediaInfo(msg);
                if (mediaType == null) continue;
                if (!ShouldDownload(monitored, mediaType.Value, fileName, fileSize)) continue;

                // Skip if we already track this message (any status except Failed, which we retry).
                var alreadyTracked = await db.DownloadTasks
                    .AnyAsync(t => t.TelegramChatId == chatId
                                   && t.MessageId == msg.ID
                                   && t.Status != DownloadStatus.Failed, cancellationToken);
                if (alreadyTracked) continue;

                var failed = await db.DownloadTasks
                    .FirstOrDefaultAsync(t => t.TelegramChatId == chatId
                                              && t.MessageId == msg.ID
                                              && t.Status == DownloadStatus.Failed, cancellationToken);
                if (failed != null) db.DownloadTasks.Remove(failed);

                var task = new DownloadTask
                {
                    TelegramChatId = chatId,
                    ChatName = monitored.Name,
                    MessageId = msg.ID,
                    FileName = fileName,
                    FileSize = fileSize,
                    MediaType = mediaType.Value,
                    Status = enqueue ? DownloadStatus.Queued : DownloadStatus.Available
                };
                db.DownloadTasks.Add(task);
                await db.SaveChangesAsync(cancellationToken);
                if (enqueue) await downloadManager.EnqueueAsync(task);
                added++;
            }

            offsetId = msgs.Last().ID;
            if (msgs.Count < batchSize) break;

            OnStatusMessage?.Invoke($"Scanning {monitored.Name}: {scanned} messages, {added} {verb}");
            await Task.Delay(300, cancellationToken);
        }

        OnStatusMessage?.Invoke($"Scan complete for {monitored.Name}: {added} media {verb} from {scanned} messages");
        _logger.LogInformation("History scan for {Chat}: scanned={Scanned}, {Verb}={Added}",
            monitored.Name, scanned, verb, added);
        return (scanned, added);
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        if (_client == null) throw new InvalidOperationException("Client not connected");

        _client.OnUpdates += OnUpdatesReceived;
        OnStatusMessage?.Invoke("Real-time monitoring started");
        _logger.LogInformation("Real-time monitoring started");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_client != null)
                _client.OnUpdates -= OnUpdatesReceived;
            _logger.LogInformation("Monitoring stopped");
        }
    }

    public Task StopMonitoringAsync()
    {
        OnStatusMessage?.Invoke("Monitoring stopped");
        return Task.CompletedTask;
    }

    private async Task OnUpdatesReceived(UpdatesBase updates)
    {
        foreach (var update in updates.UpdateList)
        {
            if (update is not UpdateNewMessage { message: Message message }) continue;

            try
            {
                await ProcessMessageAsync(message, updates);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message {MsgId}", message.ID);
            }
        }
    }

    private async Task ProcessMessageAsync(Message message, UpdatesBase updates)
    {
        var chatId = message.peer_id switch
        {
            PeerChannel pc => pc.channel_id,
            PeerChat pg => pg.chat_id,
            PeerUser pu => pu.user_id,
            _ => 0L
        };
        if (chatId == 0) return;

        // Resolve chat name
        var chatName = "Unknown";
        if (updates.Chats.TryGetValue(chatId, out var chatBase))
            chatName = chatBase.Title ?? chatName;
        else if (updates.Users.TryGetValue(chatId, out var user))
            chatName = $"{user.first_name} {user.last_name}".Trim();

        // Check if monitored
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var monitored = await db.MonitoredChats
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TelegramChatId == chatId && c.IsMonitored);

        if (monitored == null) return;

        // Get media info
        var (mediaType, fileName, fileSize) = GetMediaInfo(message);
        if (mediaType == null) return;

        // Check download policies
        if (!ShouldDownload(monitored, mediaType.Value, fileName, fileSize)) return;

        var settings = await db.Settings.FirstAsync();

        // DB-backed dedupe by (chatId, messageId)
        var existing = await db.DownloadTasks
            .AnyAsync(t => t.TelegramChatId == chatId && t.MessageId == message.ID);
        if (existing) return;

        // Create download task
        var task = new DownloadTask
        {
            TelegramChatId = chatId,
            ChatName = chatName,
            MessageId = message.ID,
            FileName = fileName,
            FileSize = fileSize,
            MediaType = mediaType.Value,
            Status = DownloadStatus.Queued
        };

        db.DownloadTasks.Add(task);
        await db.SaveChangesAsync();
        OnStatusMessage?.Invoke($"Queued: {fileName} from {chatName}");

        // Download in the background so the update loop keeps flowing, and go through the
        // download manager's slot so real-time downloads obey the same pause / concurrency
        // (and bandwidth) limits as queued ones.
        var downloadManager = _serviceProvider.GetRequiredService<IDownloadManager>();
        var taskId = task.Id;
        var chatType = monitored.ChatType;
        var reactionIcon = monitored.ReactionIcon;
        var displayName = fileName;

        _ = Task.Run(async () =>
        {
            IDisposable? slot = null;
            try
            {
                slot = await downloadManager.AcquireSlotAsync(chatId, CancellationToken.None);
                await DownloadMediaFromMessage(message, task, settings, chatType);

                using var doneScope = _serviceProvider.CreateScope();
                var doneDb = doneScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var saved = await doneDb.DownloadTasks.FindAsync(taskId);
                if (saved != null)
                {
                    saved.Status = DownloadStatus.Completed;
                    saved.CompletedAt = DateTime.UtcNow;
                    saved.DownloadedBytes = saved.FileSize;
                    saved.FilePath = task.FilePath;
                    await doneDb.SaveChangesAsync();
                }

                OnStatusMessage?.Invoke($"Downloaded: {displayName}");

                if (!string.IsNullOrEmpty(reactionIcon))
                    await ReactToMessageAsync(message, reactionIcon, updates);
            }
            catch (Exception ex)
            {
                try
                {
                    using var failScope = _serviceProvider.CreateScope();
                    var failDb = failScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var saved = await failDb.DownloadTasks.FindAsync(taskId);
                    if (saved != null)
                    {
                        saved.Status = DownloadStatus.Failed;
                        saved.ErrorMessage = ex.Message;
                        await failDb.SaveChangesAsync();
                    }
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, "Failed to persist failed status for {File}", displayName);
                }
                _logger.LogError(ex, "Download failed for {File}", displayName);
            }
            finally
            {
                slot?.Dispose();
                downloadManager.RemoveActive(taskId);
            }
        });
    }

    private async Task DownloadMediaFromMessage(Message message, DownloadTask task, AppSettings settings,
        string chatType)
    {
        if (_client == null) throw new InvalidOperationException("Client not connected");

        var filePath = FileHelper.BuildDownloadPath(settings.DownloadPath, settings.PathTemplate,
            task.ChatName, task.MediaType, task.FileName, task.MessageId, chatType);
        task.FilePath = filePath;

        var progressReport = new DownloadProgress
        {
            TaskId = task.Id,
            FileName = task.FileName,
            ChatName = task.ChatName,
            MediaType = task.MediaType,
            FileSize = task.FileSize,
            Status = DownloadStatus.Downloading
        };

        var mgr = _serviceProvider.GetRequiredService<IDownloadManager>();
        OnDownloadProgress?.Invoke(progressReport);
        mgr.ReportActiveProgress(progressReport);
        var startTime = DateTime.UtcNow;
        var limiter = _serviceProvider.GetRequiredService<BandwidthLimiter>();
        long lastTransmitted = 0;

        await using var fileStream = File.Create(filePath);

        try
        {
            switch (message.media)
            {
                case MessageMediaDocument { document: Document doc }:
                    await _client.DownloadFileAsync(doc, fileStream, (PhotoSizeBase?)null, (transmitted, total) =>
                    {
                        limiter.Throttle(transmitted - lastTransmitted);
                        lastTransmitted = transmitted;
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        progressReport.DownloadedBytes = transmitted;
                        if (total > 0) progressReport.FileSize = total;
                        if (elapsed > 0) progressReport.SpeedKbps = transmitted / elapsed / 1024;
                        progressReport.Status = DownloadStatus.Downloading;
                        OnDownloadProgress?.Invoke(progressReport);
                        mgr.ReportActiveProgress(progressReport);
                    });
                    break;

                case MessageMediaPhoto { photo: Photo photo }:
                    await _client.DownloadFileAsync(photo, fileStream, (PhotoSizeBase?)null, (transmitted, total) =>
                    {
                        limiter.Throttle(transmitted - lastTransmitted);
                        lastTransmitted = transmitted;
                        progressReport.DownloadedBytes = transmitted;
                        if (total > 0) progressReport.FileSize = total;
                        progressReport.Status = DownloadStatus.Downloading;
                        OnDownloadProgress?.Invoke(progressReport);
                        mgr.ReportActiveProgress(progressReport);
                    });
                    break;
            }

            progressReport.Status = DownloadStatus.Completed;
            OnDownloadProgress?.Invoke(progressReport);
        }
        catch
        {
            // Clean up partial file on failure
            fileStream.Close();
            if (File.Exists(filePath)) File.Delete(filePath);
            throw;
        }
    }

    public async Task DownloadFileAsync(DownloadTask task, string basePath, string pathTemplate,
        CancellationToken cancellationToken, IProgress<DownloadProgress>? progress = null)
    {
        if (_client == null) throw new InvalidOperationException("Client not connected");

        // For manual/queued downloads, we need to fetch the message first
        var dialogs = await _client.Messages_GetAllDialogs();
        InputPeer? inputPeer = null;

        foreach (var (_, chatBase) in dialogs.chats)
        {
            if (chatBase.ID == task.TelegramChatId)
            {
                inputPeer = chatBase.ToInputPeer();
                break;
            }
        }

        if (inputPeer == null)
        {
            foreach (var (_, user) in dialogs.users)
            {
                if (user.ID == task.TelegramChatId)
                {
                    inputPeer = user.ToInputPeer();
                    break;
                }
            }
        }

        if (inputPeer == null)
            throw new InvalidOperationException($"Could not resolve chat {task.TelegramChatId}");

        var messages = await _client.Messages_GetHistory(inputPeer, task.MessageId + 1, default, 0, 1, 0, 0);
        var msg = messages.Messages.OfType<Message>().FirstOrDefault(m => m.ID == task.MessageId);
        if (msg == null)
            throw new InvalidOperationException($"Message {task.MessageId} not found");

        using var lookupScope = _serviceProvider.CreateScope();
        var lookupDb = lookupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chatType = await lookupDb.MonitoredChats
            .Where(c => c.TelegramChatId == task.TelegramChatId)
            .Select(c => c.ChatType)
            .FirstOrDefaultAsync(cancellationToken) ?? "Chat";

        var filePath = FileHelper.BuildDownloadPath(basePath, pathTemplate,
            task.ChatName, task.MediaType, task.FileName, task.MessageId, chatType);
        task.FilePath = filePath;

        var progressReport = new DownloadProgress
        {
            TaskId = task.Id,
            FileName = task.FileName,
            ChatName = task.ChatName,
            MediaType = task.MediaType,
            FileSize = task.FileSize,
            Status = DownloadStatus.Downloading
        };

        var startTime = DateTime.UtcNow;
        var limiter = _serviceProvider.GetRequiredService<BandwidthLimiter>();
        long lastTransmitted = 0;
        await using var fileStream = File.Create(filePath);

        try
        {
            switch (msg.media)
            {
                case MessageMediaDocument { document: Document doc }:
                    await _client.DownloadFileAsync(doc, fileStream, (PhotoSizeBase?)null, (transmitted, total) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        limiter.Throttle(transmitted - lastTransmitted);
                        lastTransmitted = transmitted;
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        progressReport.DownloadedBytes = transmitted;
                        if (total > 0) progressReport.FileSize = total;
                        if (elapsed > 0) progressReport.SpeedKbps = transmitted / elapsed / 1024;
                        OnDownloadProgress?.Invoke(progressReport);
                        progress?.Report(progressReport);
                    });
                    break;

                case MessageMediaPhoto { photo: Photo photo }:
                    await _client.DownloadFileAsync(photo, fileStream, (PhotoSizeBase?)null, (transmitted, total) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        limiter.Throttle(transmitted - lastTransmitted);
                        lastTransmitted = transmitted;
                        progressReport.DownloadedBytes = transmitted;
                        if (total > 0) progressReport.FileSize = total;
                        OnDownloadProgress?.Invoke(progressReport);
                        progress?.Report(progressReport);
                    });
                    break;
            }

            progressReport.Status = DownloadStatus.Completed;
            OnDownloadProgress?.Invoke(progressReport);
            progress?.Report(progressReport);
        }
        catch
        {
            fileStream.Close();
            if (File.Exists(filePath)) File.Delete(filePath);
            throw;
        }
    }

    private (MediaType? type, string fileName, long size) GetMediaInfo(Message message)
    {
        return message.media switch
        {
            MessageMediaPhoto { photo: Photo photo } =>
                (MediaType.Photo, $"{photo.id}.jpg", photo.sizes?.OfType<PhotoSizeProgressive>()
                    .FirstOrDefault()?.sizes.LastOrDefault() ?? 0),

            MessageMediaDocument { document: Document doc } =>
                (FileHelper.GetMediaTypeFromMime(doc.mime_type),
                 doc.Filename ?? $"{doc.id}{GetExtension(doc.mime_type)}",
                 doc.size),

            _ => (null, string.Empty, 0)
        };
    }

    private static string GetExtension(string? mimeType) => mimeType switch
    {
        "video/mp4" => ".mp4",
        "video/x-matroska" => ".mkv",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/mp4" => ".m4a",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        _ => ".bin"
    };

    private static bool ShouldDownload(MonitoredChat chat, MediaType type, string fileName, long size)
    {
        var allowed = type switch
        {
            MediaType.Video => chat.DownloadVideos,
            MediaType.Photo => chat.DownloadPhotos,
            MediaType.Music => chat.DownloadMusic,
            MediaType.File => chat.DownloadFiles,
            _ => false
        };
        if (!allowed) return false;

        var sizeMb = size / (1024.0 * 1024);
        if (chat.MinFileSizeMb > 0 && sizeMb < chat.MinFileSizeMb) return false;
        if (chat.MaxFileSizeMb > 0 && sizeMb > chat.MaxFileSizeMb) return false;

        if (!string.IsNullOrEmpty(chat.IgnorePatterns))
        {
            try
            {
                var patterns = System.Text.Json.JsonSerializer.Deserialize<string[]>(chat.IgnorePatterns);
                if (patterns != null)
                {
                    foreach (var pattern in patterns)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(fileName, pattern))
                            return false;
                    }
                }
            }
            catch { }
        }

        return true;
    }

    private async Task ReactToMessageAsync(Message message, string emoji, UpdatesBase updates)
    {
        try
        {
            if (_client == null) return;
            var chatId = message.peer_id switch
            {
                PeerChannel pc => pc.channel_id,
                PeerChat pg => pg.chat_id,
                PeerUser pu => pu.user_id,
                _ => 0L
            };

            InputPeer? inputPeer = null;
            if (updates.Chats.TryGetValue(chatId, out var rChatBase))
                inputPeer = rChatBase.ToInputPeer();
            else if (updates.Users.TryGetValue(chatId, out var rUser))
                inputPeer = rUser.ToInputPeer();

            if (inputPeer != null)
            {
                await _client.Messages_SendReaction(inputPeer, message.ID,
                    new[] { new ReactionEmoji { emoticon = emoji } });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send reaction");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            await Task.Run(() => _client.Dispose());
            _client = null;
            _currentUser = null;
            AuthState = AuthState.NotAuthenticated;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
