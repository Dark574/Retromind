using Avalonia.Threading;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeGamepadDispatchTests
{
    [Fact]
    public async Task LifecycleRelease_IsQueuedAfterThePollingPressEvenWhenCalledFromUiThread()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var directions = new List<bool>();
                Assert.True(dispatcher.CheckAccess());
                Task.Run(() => BigModeViewModel.DispatchGamepadAction(() => directions.Add(true), dispatcher))
                    .GetAwaiter().GetResult();
                BigModeViewModel.DispatchGamepadAction(() => directions.Add(false), dispatcher);

                Assert.Empty(directions); // UI-thread calls must not bypass previously queued events.
                dispatcher.RunJobs();
                Assert.Equal([true, false], directions);
                dispatcher.InvokeShutdown();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true };
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
