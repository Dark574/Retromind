using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class LibrarySaveSequencerTests
{
    [Fact]
    public async Task RunAsync_DoesNotStartSecondPipelineBeforeFirstCompletes()
    {
        var sequencer = new LibrarySaveSequencer();
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new List<string>();

        var first = sequencer.RunAsync(async () =>
        {
            executionOrder.Add("first-start");
            await releaseFirst.Task;
            executionOrder.Add("first-end");
        });

        var second = sequencer.RunAsync(() =>
        {
            executionOrder.Add("second");
            return Task.CompletedTask;
        });

        Assert.Equal(["first-start"], executionOrder);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first-start", "first-end", "second"], executionOrder);
    }

    [Fact]
    public async Task RunAsync_ReleasesGateAfterFailure()
    {
        var sequencer = new LibrarySaveSequencer();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sequencer.RunAsync(() => throw new InvalidOperationException("save failed")));

        var nextPipelineRan = false;
        await sequencer.RunAsync(() =>
        {
            nextPipelineRan = true;
            return Task.CompletedTask;
        });

        Assert.True(nextPipelineRan);
    }
}
