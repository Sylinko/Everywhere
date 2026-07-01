using System.Diagnostics;

namespace Everywhere.Common;

/// <summary>
/// A token-bucket based bandwidth rate limiter that produces smooth, non-bursty throttling.
/// Unlike PWM-style implementations that alternate between full-speed bursts and long pauses,
/// this limiter uses constant-rate token replenishment with precise sleep timing to let TCP
/// congestion control naturally converge on the target rate.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly long _bytesPerSecond;
    private readonly long _maxBurstBytes;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _lastTimestamp;

    /// <param name="bytesPerSecond">Target throughput in bytes per second.</param>
    /// <param name="maxBurstBytes">
    /// Maximum burst size (bucket capacity) in bytes. Defaults to 2× the per-second rate,
    /// allowing brief bursts while maintaining long-term average.
    /// </param>
    public TokenBucketRateLimiter(long bytesPerSecond, long? maxBurstBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);

        _bytesPerSecond = bytesPerSecond;
        _maxBurstBytes = maxBurstBytes ?? bytesPerSecond * 2;
        _tokens = _maxBurstBytes;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Acquires permission to consume up to <paramref name="requestedBytes"/> bytes.
    /// Returns the actual number of bytes allowed to consume, waiting as needed to
    /// maintain the target rate — but never longer than necessary.
    /// </summary>
    public async ValueTask<int> AcquireAsync(int requestedBytes, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan waitTime;

            lock (_lock)
            {
                ReplenishTokens();
                var allowedTokens = (int)Math.Min(requestedBytes, _tokens);
                _tokens -= allowedTokens;

                // If we can serve at least 1 byte, proceed immediately.
                if (allowedTokens > 0)
                {
                    return allowedTokens;
                }

                // Compute how long until at least 1 token is available.
                var deficit = 1.0 - _tokens;
                waitTime = TimeSpan.FromSeconds(deficit / _bytesPerSecond);
            }

            // Wait precisely the computed time, then retry.
            // Using Task.Delay with the exact deficit time avoids PWM-style long sleeps.
            await Task.Delay(waitTime, cancellationToken);
        }
    }

    /// <summary>
    /// Returns unused tokens back to the bucket (up to the max burst limit).
    /// Call this after a read returns fewer bytes than <see cref="AcquireAsync"/> granted.
    /// </summary>
    public void ReturnUnused(int unusedBytes)
    {
        if (unusedBytes <= 0) return;

        lock (_lock)
        {
            _tokens = Math.Min(_tokens + unusedBytes, _maxBurstBytes);
        }
    }

    /// <summary>
    /// Replenishes tokens based on elapsed time since last replenishment.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void ReplenishTokens()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;

        var newTokens = elapsed.TotalSeconds * _bytesPerSecond;
        _tokens = Math.Min(_tokens + newTokens, _maxBurstBytes);
    }
}
