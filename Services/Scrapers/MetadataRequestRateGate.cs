using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Spaces request starts for one provider instance without keeping an HTTP
/// request serialized for its entire lifetime.
/// </summary>
internal sealed class MetadataRequestRateGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _minimumIntervalTicks;
    private long _nextRequestTimestamp;

    public MetadataRequestRateGate(TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));

        _minimumIntervalTicks = (long)Math.Ceiling(
            minimumInterval.TotalSeconds * Stopwatch.Frequency);
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                var remainingTicks = _nextRequestTimestamp - now;
                if (remainingTicks <= 0)
                    break;

                var delay = TimeSpan.FromSeconds(
                    (double)remainingTicks / Stopwatch.Frequency);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            _nextRequestTimestamp = Stopwatch.GetTimestamp() + _minimumIntervalTicks;
        }
        finally
        {
            _gate.Release();
        }
    }
}
