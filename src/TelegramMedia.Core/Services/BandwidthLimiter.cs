namespace TelegramMedia.Core.Services;

/// <summary>
/// Global, thread-safe download speed limiter shared by every active download.
/// Uses a token-bucket: call <see cref="Throttle"/> with the bytes just transferred
/// and it blocks just long enough to keep the aggregate rate under the limit.
/// A limit of 0 (or less) means unlimited.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly object _lock = new();
    private long _bytesPerSecond;   // 0 = unlimited
    private double _allowance;      // bytes currently permitted to send
    private DateTime _last = DateTime.UtcNow;

    public int LimitKbps
    {
        get { lock (_lock) return (int)(_bytesPerSecond / 1024); }
    }

    public void SetLimitKbps(int kbps)
    {
        var bps = kbps <= 0 ? 0L : (long)kbps * 1024;
        lock (_lock)
        {
            _bytesPerSecond = bps;
            _allowance = bps;          // reset the burst budget
            _last = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Account for <paramref name="bytes"/> just transferred, sleeping if the rate
    /// would exceed the configured limit. Called from the (synchronous) download
    /// progress callback, so it blocks the calling thread on purpose.
    /// </summary>
    public void Throttle(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            if (_bytesPerSecond <= 0) return; // unlimited

            var now = DateTime.UtcNow;
            _allowance += (now - _last).TotalSeconds * _bytesPerSecond;
            _last = now;

            // Never allow more than ~1s worth of burst to accumulate.
            if (_allowance > _bytesPerSecond) _allowance = _bytesPerSecond;

            _allowance -= bytes;
            if (_allowance < 0)
            {
                var seconds = -_allowance / _bytesPerSecond;
                // Clamp any pathological sleep (e.g. a huge chunk against a tiny limit).
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(seconds, 5)));
                _allowance = 0;
            }
        }
    }
}
