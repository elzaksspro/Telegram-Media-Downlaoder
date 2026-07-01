using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramMedia.Core.Data;
using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Interfaces;
using TelegramMedia.Core.Models;
using TelegramMedia.Core.Services;

namespace TelegramMedia.Telegram.Services;

/// <summary>
/// Central download coordinator. Pending tasks live in an ordered list and a single
/// dispatcher loop pulls the highest-priority eligible one whenever a concurrency slot
/// frees up — so priority, pause (global + per-chat) and the speed limit are all honoured
/// in one place. The real-time monitor shares the same slots via <see cref="AcquireSlotAsync"/>.
/// </summary>
public class DownloadManager : IDownloadManager
{
    private const int MaxConcurrencyCap = 32;

    private readonly ITelegramClientService _telegramClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadManager> _logger;
    private readonly BandwidthLimiter _bandwidth;

    // Concurrency limiter. Created with room to grow so permits can be added live.
    private readonly SemaphoreSlim _semaphore = new(3, MaxConcurrencyCap);
    private readonly object _concLock = new();
    private int _maxConcurrent = 3;

    // Global soft-pause gate.
    private volatile bool _isPaused;
    private readonly object _gateLock = new();
    private TaskCompletionSource _resumeSignal;

    // Per-chat soft pause.
    private readonly Dictionary<long, TaskCompletionSource> _chatGates = new();
    private readonly object _chatLock = new();

    // Pending (not yet running) tasks, ordered on demand by (Priority, Id).
    private readonly List<DownloadTask> _pending = new();
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _workAvailable = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private int _dispatcherStarted;

    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private readonly Dictionary<int, CancellationTokenSource> _activeCts = new();
    private readonly List<DownloadProgress> _activeDownloads = new();

    public IReadOnlyList<DownloadProgress> ActiveDownloads
    {
        get { lock (_activeDownloads) return _activeDownloads.ToList(); }
    }
    public bool IsPaused => _isPaused;
    public int MaxConcurrent { get { lock (_concLock) return _maxConcurrent; } }
    public int BandwidthLimitKbps => _bandwidth.LimitKbps;
    public event Action? OnStateChanged;

    public DownloadManager(
        ITelegramClientService telegramClient,
        IServiceProvider serviceProvider,
        ILogger<DownloadManager> logger,
        BandwidthLimiter bandwidth)
    {
        _telegramClient = telegramClient;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _bandwidth = bandwidth;
        _resumeSignal = CreateCompletedSignal();
    }

    // --- Slot acquisition (pause + concurrency), shared with the real-time monitor ---

    public Task<IDisposable> AcquireSlotAsync(CancellationToken cancellationToken)
        => AcquireSlotAsync(0, cancellationToken);

    public async Task<IDisposable> AcquireSlotAsync(long telegramChatId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync();
        while (true)
        {
            await WaitWhilePausedAsync(cancellationToken);
            if (telegramChatId != 0) await WaitWhileChatPausedAsync(telegramChatId, cancellationToken);
            if (!_isPaused && (telegramChatId == 0 || !IsChatPaused(telegramChatId)))
                break;
        }
        await _semaphore.WaitAsync(cancellationToken);
        return new SlotReleaser(_semaphore);
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (_isPaused)
        {
            Task wait;
            lock (_gateLock) wait = _resumeSignal.Task;
            await wait.WaitAsync(cancellationToken);
        }
    }

    private async Task WaitWhileChatPausedAsync(long chatId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_chatLock)
            {
                if (!_chatGates.TryGetValue(chatId, out var tcs) || tcs.Task.IsCompleted)
                    return;
                wait = tcs.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    private sealed class SlotReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public SlotReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose()
        {
            _semaphore?.Release();
            _semaphore = null;
        }
    }

    private static TaskCompletionSource CreateCompletedSignal()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    // --- Dispatcher ---

    private void EnsureDispatcher()
    {
        if (Interlocked.Exchange(ref _dispatcherStarted, 1) == 0)
            _ = Task.Run(DispatchLoopAsync);
    }

    private void SignalWork()
    {
        if (_workAvailable.CurrentCount == 0)
        {
            try { _workAvailable.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task DispatchLoopAsync()
    {
        try { await EnsureInitializedAsync(); } catch (Exception ex) { _logger.LogError(ex, "Dispatcher init failed"); }

        while (!_shutdown.IsCancellationRequested)
        {
            IDisposable slot;
            try { slot = await AcquireSlotAsync(0, _shutdown.Token); }
            catch (OperationCanceledException) { break; }

            var next = TakeNextEligible();
            if (next == null)
            {
                slot.Dispose();
                try { await _workAvailable.WaitAsync(_shutdown.Token); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _ = RunDownloadAsync(next, slot);
        }
    }

    private DownloadTask? TakeNextEligible()
    {
        lock (_pendingLock)
        {
            DownloadTask? best = null;
            foreach (var t in _pending)
            {
                if (IsChatPaused(t.TelegramChatId)) continue;
                if (best == null || t.Priority < best.Priority ||
                    (t.Priority == best.Priority && t.Id < best.Id))
                    best = t;
            }
            if (best != null) _pending.Remove(best);
            return best;
        }
    }

    private async Task RunDownloadAsync(DownloadTask task, IDisposable slot)
    {
        var cts = new CancellationTokenSource();
        lock (_activeCts) _activeCts[task.Id] = cts;

        var progress = new DownloadProgress
        {
            TaskId = task.Id,
            FileName = task.FileName,
            ChatName = task.ChatName,
            MediaType = task.MediaType,
            FileSize = task.FileSize,
            Status = DownloadStatus.Downloading
        };
        lock (_activeDownloads)
        {
            _activeDownloads.RemoveAll(p => p.TaskId == task.Id);
            _activeDownloads.Add(progress);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.Settings.FirstAsync();

            await UpdateDbStatusAsync(db, task.Id, DownloadStatus.Downloading);
            OnStateChanged?.Invoke();

            var reportProgress = new Progress<DownloadProgress>(p =>
            {
                lock (_activeDownloads)
                {
                    var existing = _activeDownloads.FirstOrDefault(x => x.TaskId == p.TaskId);
                    if (existing != null)
                    {
                        existing.DownloadedBytes = p.DownloadedBytes;
                        existing.FileSize = p.FileSize;
                        existing.SpeedKbps = p.SpeedKbps;
                        existing.Status = p.Status;
                        existing.ErrorMessage = p.ErrorMessage;
                    }
                }
                OnStateChanged?.Invoke();
            });

            await _telegramClient.DownloadFileAsync(task, settings.DownloadPath,
                settings.PathTemplate, cts.Token, reportProgress);

            await UpdateDbStatusAsync(db, task.Id, DownloadStatus.Completed, completed: true);
            lock (_activeDownloads) _activeDownloads.RemoveAll(p => p.TaskId == task.Id);
        }
        catch (OperationCanceledException)
        {
            // Pause/Cancel already set the DB status; just drop the live row.
            lock (_activeDownloads) _activeDownloads.RemoveAll(p => p.TaskId == task.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for task {TaskId}", task.Id);
            SetMemoryStatus(task.Id, DownloadStatus.Failed, ex.Message);
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await UpdateDbStatusAsync(db, task.Id, DownloadStatus.Failed, error: ex.Message);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Failed to persist failed status for task {TaskId}", task.Id);
            }
        }
        finally
        {
            slot.Dispose();
            lock (_activeCts) _activeCts.Remove(task.Id);
            OnStateChanged?.Invoke();
            SignalWork();
        }
    }

    private void SetMemoryStatus(int taskId, DownloadStatus status, string? error = null)
    {
        lock (_activeDownloads)
        {
            var p = _activeDownloads.FirstOrDefault(x => x.TaskId == taskId);
            if (p != null)
            {
                p.Status = status;
                p.ErrorMessage = error;
            }
        }
    }

    private static async Task UpdateDbStatusAsync(AppDbContext db, int taskId, DownloadStatus status,
        bool completed = false, string? error = null)
    {
        var t = await db.DownloadTasks.FindAsync(taskId);
        if (t == null) return;
        t.Status = status;
        if (error != null) t.ErrorMessage = error;
        if (completed)
        {
            t.CompletedAt = DateTime.UtcNow;
            t.DownloadedBytes = t.FileSize;
        }
        await db.SaveChangesAsync();
    }

    // --- Enqueue / batch operations ---

    public async Task EnqueueAsync(DownloadTask task)
    {
        await EnsureInitializedAsync();
        EnsureDispatcher();

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await UpdateDbStatusAsync(db, task.Id, DownloadStatus.Queued);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark task {TaskId} queued", task.Id);
        }

        lock (_pendingLock)
        {
            _pending.RemoveAll(t => t.Id == task.Id);
            _pending.Add(task);
        }
        OnStateChanged?.Invoke();
        SignalWork();
    }

    public async Task StartTasksAsync(IEnumerable<int> taskIds)
    {
        var ids = taskIds.Distinct().ToList();
        if (ids.Count == 0) return;
        await EnsureInitializedAsync();
        EnsureDispatcher();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tasks = await db.DownloadTasks.Where(t => ids.Contains(t.Id)).ToListAsync();

        var toQueue = new List<DownloadTask>();
        foreach (var t in tasks)
        {
            if (t.Status is DownloadStatus.Completed or DownloadStatus.Downloading) continue;
            t.Status = DownloadStatus.Queued;
            toQueue.Add(t);
        }
        await db.SaveChangesAsync();

        lock (_pendingLock)
        {
            foreach (var t in toQueue)
            {
                _pending.RemoveAll(p => p.Id == t.Id);
                _pending.Add(t);
            }
        }
        OnStateChanged?.Invoke();
        SignalWork();
    }

    public async Task PauseTasksAsync(IEnumerable<int> taskIds)
        => await StopTasksAsync(taskIds, DownloadStatus.Paused);

    public async Task CancelTasksAsync(IEnumerable<int> taskIds)
        => await StopTasksAsync(taskIds, DownloadStatus.Available);

    private async Task StopTasksAsync(IEnumerable<int> taskIds, DownloadStatus newStatus)
    {
        var ids = taskIds.Distinct().ToHashSet();
        if (ids.Count == 0) return;

        // Remove any that are still pending (not yet running).
        lock (_pendingLock) _pending.RemoveAll(t => ids.Contains(t.Id));

        // Persist the new status for all of them.
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tasks = await db.DownloadTasks.Where(t => ids.Contains(t.Id)).ToListAsync();
            foreach (var t in tasks)
                if (t.Status != DownloadStatus.Completed)
                    t.Status = newStatus;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist stop status {Status}", newStatus);
        }

        // Cancel any that are actively downloading.
        foreach (var id in ids)
        {
            CancellationTokenSource? cts;
            lock (_activeCts) _activeCts.TryGetValue(id, out cts);
            cts?.Cancel();
        }
        lock (_activeDownloads) _activeDownloads.RemoveAll(p => ids.Contains(p.TaskId));

        OnStateChanged?.Invoke();
        SignalWork();
    }

    public async Task SetPriorityAsync(int taskId, int direction)
    {
        int idA = 0, idB = 0, prA = 0, prB = 0;
        lock (_pendingLock)
        {
            var item = _pending.FirstOrDefault(t => t.Id == taskId);
            if (item == null) return;

            var siblings = _pending
                .Where(t => t.TelegramChatId == item.TelegramChatId)
                .OrderBy(t => t.Priority).ThenBy(t => t.Id)
                .ToList();
            var idx = siblings.IndexOf(item);
            var swapIdx = idx + (direction < 0 ? -1 : 1);
            if (swapIdx < 0 || swapIdx >= siblings.Count) return;

            var other = siblings[swapIdx];
            (item.Priority, other.Priority) = (other.Priority, item.Priority);
            if (item.Priority == other.Priority)
            {
                // Break ties (all-zero default) so the swap actually reorders.
                if (direction < 0) item.Priority = other.Priority - 1;
                else item.Priority = other.Priority + 1;
            }
            idA = item.Id; prA = item.Priority;
            idB = other.Id; prB = other.Priority;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.DownloadTasks.FindAsync(idA);
            var b = await db.DownloadTasks.FindAsync(idB);
            if (a != null) a.Priority = prA;
            if (b != null) b.Priority = prB;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist priority change for task {TaskId}", taskId);
        }

        OnStateChanged?.Invoke();
    }

    public async Task MoveToEdgeAsync(int taskId, bool toTop)
    {
        int id = 0, priority = 0;
        lock (_pendingLock)
        {
            var item = _pending.FirstOrDefault(t => t.Id == taskId);
            if (item == null) return;

            var siblings = _pending.Where(t => t.TelegramChatId == item.TelegramChatId).ToList();
            if (toTop)
                item.Priority = siblings.Min(t => t.Priority) - 1;
            else
                item.Priority = siblings.Max(t => t.Priority) + 1;
            id = item.Id;
            priority = item.Priority;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = await db.DownloadTasks.FindAsync(id);
            if (t != null) { t.Priority = priority; await db.SaveChangesAsync(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist move-to-edge for task {TaskId}", taskId);
        }

        OnStateChanged?.Invoke();
    }

    // --- Live rows for externally-run downloads (real-time monitor) ---

    public void ReportActiveProgress(DownloadProgress progress)
    {
        lock (_activeDownloads)
        {
            var existing = _activeDownloads.FirstOrDefault(x => x.TaskId == progress.TaskId);
            if (existing == null)
                _activeDownloads.Add(progress);
            else
            {
                existing.DownloadedBytes = progress.DownloadedBytes;
                existing.FileSize = progress.FileSize;
                existing.SpeedKbps = progress.SpeedKbps;
                existing.Status = progress.Status;
                existing.ErrorMessage = progress.ErrorMessage;
            }
        }
        OnStateChanged?.Invoke();
    }

    public void RemoveActive(int taskId)
    {
        lock (_activeDownloads) _activeDownloads.RemoveAll(p => p.TaskId == taskId);
        OnStateChanged?.Invoke();
    }

    // --- Pause / resume (global soft) ---

    public Task PauseAllAsync()
    {
        _isPaused = true;
        lock (_gateLock)
        {
            if (_resumeSignal.Task.IsCompleted)
                _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        OnStateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task ResumeAllAsync()
    {
        _isPaused = false;
        lock (_gateLock) _resumeSignal.TrySetResult();
        OnStateChanged?.Invoke();
        SignalWork();
        return Task.CompletedTask;
    }

    // --- Per-chat pause ---

    public bool IsChatPaused(long telegramChatId)
    {
        lock (_chatLock)
            return _chatGates.TryGetValue(telegramChatId, out var tcs) && !tcs.Task.IsCompleted;
    }

    public async Task PauseChatAsync(long telegramChatId)
    {
        lock (_chatLock)
        {
            if (!_chatGates.TryGetValue(telegramChatId, out var tcs) || tcs.Task.IsCompleted)
                _chatGates[telegramChatId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await SetChatPausedInDbAsync(telegramChatId, true);
        OnStateChanged?.Invoke();
    }

    public async Task ResumeChatAsync(long telegramChatId)
    {
        lock (_chatLock)
        {
            if (_chatGates.TryGetValue(telegramChatId, out var tcs))
            {
                tcs.TrySetResult();
                _chatGates.Remove(telegramChatId);
            }
        }
        await SetChatPausedInDbAsync(telegramChatId, false);
        OnStateChanged?.Invoke();
        SignalWork();
    }

    private async Task SetChatPausedInDbAsync(long telegramChatId, bool paused)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var chat = await db.MonitoredChats.FirstOrDefaultAsync(c => c.TelegramChatId == telegramChatId);
            if (chat != null && chat.IsPaused != paused)
            {
                chat.IsPaused = paused;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist pause state for chat {ChatId}", telegramChatId);
        }
    }

    // --- Live concurrency / bandwidth ---

    public async Task SetMaxConcurrentAsync(int max)
    {
        max = Math.Clamp(max, 1, MaxConcurrencyCap);
        int delta;
        lock (_concLock)
        {
            delta = max - _maxConcurrent;
            _maxConcurrent = max;
        }

        if (delta > 0)
        {
            _semaphore.Release(delta);
        }
        else if (delta < 0)
        {
            var toReclaim = -delta;
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < toReclaim; i++)
                    await _semaphore.WaitAsync();
            });
        }

        await PersistSettingAsync(s => s.MaxConcurrentDownloads = max);
        OnStateChanged?.Invoke();
        SignalWork();
    }

    public async Task SetBandwidthLimitAsync(int kbps)
    {
        if (kbps < 0) kbps = 0;
        _bandwidth.SetLimitKbps(kbps);
        await PersistSettingAsync(s => s.MaxSpeedKbps = kbps);
        OnStateChanged?.Invoke();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.Settings.FirstOrDefaultAsync();
            if (settings != null)
            {
                var desired = Math.Clamp(settings.MaxConcurrentDownloads <= 0 ? 3 : settings.MaxConcurrentDownloads,
                    1, MaxConcurrencyCap);
                int delta;
                lock (_concLock)
                {
                    delta = desired - _maxConcurrent;
                    _maxConcurrent = desired;
                }
                if (delta > 0) _semaphore.Release(delta);
                else if (delta < 0) for (var i = 0; i < -delta; i++) await _semaphore.WaitAsync();

                _bandwidth.SetLimitKbps(settings.MaxSpeedKbps);
            }

            var pausedChatIds = await db.MonitoredChats
                .Where(c => c.IsPaused)
                .Select(c => c.TelegramChatId)
                .ToListAsync();
            lock (_chatLock)
            {
                foreach (var id in pausedChatIds)
                    if (!_chatGates.TryGetValue(id, out var tcs) || tcs.Task.IsCompleted)
                        _chatGates[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task PersistSettingAsync(Action<AppSettings> mutate)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.Settings.FirstOrDefaultAsync();
            if (settings != null)
            {
                mutate(settings);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist download setting");
        }
    }

    // --- Legacy single-item controls (kept for compatibility) ---

    public Task PauseAsync(int taskId) => PauseTasksAsync(new[] { taskId });
    public Task ResumeAsync(int taskId) => StartTasksAsync(new[] { taskId });
    public Task CancelAsync(int taskId) => CancelTasksAsync(new[] { taskId });

    public async Task<int> PurgeChatAsync(long telegramChatId, MediaType? mediaType = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var activeStatuses = new[]
        {
            DownloadStatus.Queued, DownloadStatus.Downloading,
            DownloadStatus.Paused, DownloadStatus.Available
        };
        var query = db.DownloadTasks
            .Where(t => t.TelegramChatId == telegramChatId && activeStatuses.Contains(t.Status));
        if (mediaType.HasValue)
            query = query.Where(t => t.MediaType == mediaType.Value);

        var tasks = await query.ToListAsync();
        var ids = tasks.Select(t => t.Id).ToHashSet();

        lock (_pendingLock) _pending.RemoveAll(t => ids.Contains(t.Id));
        foreach (var id in ids)
        {
            CancellationTokenSource? cts;
            lock (_activeCts) _activeCts.TryGetValue(id, out cts);
            cts?.Cancel();
        }
        lock (_activeDownloads) _activeDownloads.RemoveAll(p => ids.Contains(p.TaskId));

        db.DownloadTasks.RemoveRange(tasks);
        await db.SaveChangesAsync();

        OnStateChanged?.Invoke();
        _logger.LogInformation("Purged {Count} tasks for chat {ChatId} (mediaType={Type})",
            tasks.Count, telegramChatId, mediaType?.ToString() ?? "all");
        return tasks.Count;
    }
}
