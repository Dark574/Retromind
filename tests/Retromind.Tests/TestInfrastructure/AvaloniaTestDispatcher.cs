using Avalonia.Threading;

namespace Retromind.Tests.TestInfrastructure;

internal static class AvaloniaTestDispatcher
{
    // Avalonia transitions validate against the global UI dispatcher even when
    // controls have another owning dispatcher. Keep a real managed UI loop alive
    // for all tests, without a windowing backend or an additional test dependency.
    private static readonly Dispatcher Instance = Start();

    internal static void EnsureStarted() { _ = Instance; }

    internal static async Task InvokeAsync(Action<Dispatcher> action)
        => await Instance.InvokeAsync(() => action(Instance));

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            var dispatcher = Dispatcher.UIThread;
            ready.SetResult(dispatcher);
            dispatcher.MainLoop(CancellationToken.None);
        }) { IsBackground = true, Name = "Retromind test UI" }.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
