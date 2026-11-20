using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Downloads;

// Spreads network reads over elapsed monotonic time. At most one buffered read can get ahead.
internal sealed class DownloadRateLimiter(long bytesPerSecond, TimeProvider time)
{
    private readonly long _started = time.GetTimestamp();
    private TimeSpan _next;

    internal Task WaitAsync(int bytes, CancellationToken ct)
    {
        var elapsed = time.GetElapsedTime(_started);
        var duration = TimeSpan.FromSeconds((double)bytes / bytesPerSecond);
        var available = elapsed - duration;
        if (_next < available)
            _next = available;
        _next += duration;

        var delay = _next - elapsed;
        return delay > TimeSpan.Zero ? Task.Delay(delay, time, ct) : Task.CompletedTask;
    }
}
