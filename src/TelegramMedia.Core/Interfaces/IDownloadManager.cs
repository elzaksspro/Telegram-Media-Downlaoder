using TelegramMedia.Core.Enums;
using TelegramMedia.Core.Models;

namespace TelegramMedia.Core.Interfaces;

public interface IDownloadManager
{
    IReadOnlyList<DownloadProgress> ActiveDownloads { get; }

    /// <summary>True when the whole pipeline is paused (no new downloads start).</summary>
    bool IsPaused { get; }

    /// <summary>Maximum number of downloads allowed to run at once.</summary>
    int MaxConcurrent { get; }

    /// <summary>Global speed cap in KB/s (0 = unlimited).</summary>
    int BandwidthLimitKbps { get; }

    event Action? OnStateChanged;

    Task EnqueueAsync(DownloadTask task);
    Task PauseAsync(int taskId);
    Task ResumeAsync(int taskId);
    Task CancelAsync(int taskId);
    Task PauseAllAsync();
    Task ResumeAllAsync();

    /// <summary>True when downloads for a specific chat are paused.</summary>
    bool IsChatPaused(long telegramChatId);

    /// <summary>Pause only this chat's downloads; other chats keep running.</summary>
    Task PauseChatAsync(long telegramChatId);

    /// <summary>Resume a chat previously paused with <see cref="PauseChatAsync"/>.</summary>
    Task ResumeChatAsync(long telegramChatId);

    /// <summary>Queue these tasks for download (from Available/Paused/Failed). Skips completed/running.</summary>
    Task StartTasksAsync(IEnumerable<int> taskIds);

    /// <summary>Stop these tasks (running or pending) and mark them Paused.</summary>
    Task PauseTasksAsync(IEnumerable<int> taskIds);

    /// <summary>Stop these tasks and return them to the Available list (un-queue).</summary>
    Task CancelTasksAsync(IEnumerable<int> taskIds);

    /// <summary>Move a pending task earlier (direction &lt; 0) or later (direction &gt; 0) in its chat's queue.</summary>
    Task SetPriorityAsync(int taskId, int direction);

    /// <summary>Move a pending task to the very front (toTop=true) or back of its chat's queue.</summary>
    Task MoveToEdgeAsync(int taskId, bool toTop);

    /// <summary>Register/update a live download row shown in the Active list (used by the real-time monitor).</summary>
    void ReportActiveProgress(DownloadProgress progress);

    /// <summary>Remove a live download row from the Active list.</summary>
    void RemoveActive(int taskId);

    /// <summary>Change the concurrency limit live. Applies to running and future downloads.</summary>
    Task SetMaxConcurrentAsync(int max);

    /// <summary>Change the global speed cap live (KB/s, 0 = unlimited).</summary>
    Task SetBandwidthLimitAsync(int kbps);

    /// <summary>
    /// Wait for a download slot, honouring the pause state and concurrency limit.
    /// Dispose the returned handle to release the slot. Used by the real-time
    /// monitor so its downloads obey the same pause/concurrency rules as the queue.
    /// </summary>
    Task<IDisposable> AcquireSlotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="AcquireSlotAsync(CancellationToken)"/> but also waits while the
    /// given chat is individually paused.
    /// </summary>
    Task<IDisposable> AcquireSlotAsync(long telegramChatId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel active downloads for a chat and delete queued/paused DB tasks.
    /// Pass a MediaType to restrict to that type only.
    /// Completed and Failed tasks are preserved as history.
    /// </summary>
    Task<int> PurgeChatAsync(long telegramChatId, MediaType? mediaType = null);
}
