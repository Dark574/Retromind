using System.Diagnostics;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class MetadataRequestRateGateTests
{
    [Fact]
    public async Task WaitAsync_SpacesConcurrentRequestStarts()
    {
        var gate = new MetadataRequestRateGate(TimeSpan.FromMilliseconds(40));
        var starts = new long[3];

        await Task.WhenAll(Enumerable.Range(0, starts.Length).Select(async index =>
        {
            await gate.WaitAsync();
            starts[index] = Stopwatch.GetTimestamp();
        }));

        Array.Sort(starts);
        var elapsed = Stopwatch.GetElapsedTime(starts[0], starts[^1]);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(70),
            $"Expected spaced request starts, observed {elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task WaitAsync_CancellationWhileQueued_DoesNotBlockFollowingRequests()
    {
        var gate = new MetadataRequestRateGate(TimeSpan.FromMilliseconds(100));
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync(cancellation.Token));

        await gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }
}
