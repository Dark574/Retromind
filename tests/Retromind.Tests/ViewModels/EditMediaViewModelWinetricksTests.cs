using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class EditMediaViewModelWinetricksTests : IDisposable
{
    private readonly TemporaryDirectory _diagnosticRoot = new();
    private readonly EnvironmentVariableScope _dataRootEnvironment;

    public EditMediaViewModelWinetricksTests()
    {
        _dataRootEnvironment = new EnvironmentVariableScope(("APPIMAGE", _diagnosticRoot.GetPath("Retromind.AppImage")));
    }

    public void Dispose()
    {
        _dataRootEnvironment.Dispose();
        _diagnosticRoot.Dispose();
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("ProcessExecution")]
    [InlineData("ProcessStart")]
    [InlineData("Preparation")]
    [InlineData("Unexpected")]
    public async Task ExecuteWinetricksWithLog_ResetsBothRunningStatesAndMarksFailuresInLog(string outcome)
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var starter = WinetricksTestProgram.CreateStarter(temp, outcome == "ProcessExecution" ? "exit 17" : "exit 0");
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            info => outcome == "ProcessStart" ? null : starter(info));
        var vm = CreateShell(service);
        using var log = new ProcessLogViewModel("Winetricks", newestFirst: true);
        var request = Services.WinetricksServiceTests.Request(temp);
        if (outcome == "Preparation")
            request = request with { PrefixRoot = temp.CreateFile("blocked-prefix") };
        if (outcome == "Unexpected")
            request = null!; // Exercise the UI's last-resort exception boundary too.
        var dispatchLock = new object();
        void Dispatch(Action action) { lock (dispatchLock) action(); }

        await vm.ExecuteWinetricksWithLogAsync(request, log, log.Token, Dispatch);

        Assert.False(vm.IsWinetricksRunning);
        Assert.False(log.IsRunning);
        Assert.False(log.CanCancel);
        Assert.False(log.IsCancelled);
        Assert.Equal("Finished.", log.StatusText);
        Assert.False(log.CancelCommand.CanExecute(null));
        if (outcome == "Success")
        {
            Assert.DoesNotContain("[ERROR]", log.LogText);
            Assert.Empty(errors.Reports);
        }
        else
        {
            Assert.StartsWith("[ERROR] Winetricks", log.LogText, StringComparison.Ordinal);
            if (outcome != "Unexpected")
            {
                Assert.Contains(outcome, log.LogText);
                Assert.Single(errors.Reports);
            }
        }
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWinetricksWithLog_ExistingCancelOrWindowCloseStopsProcessAndResetsStatus(bool closeWindow)
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'READY\\n'\nexec /bin/sleep 60"));
        var vm = CreateShell(service);
        using var log = new ProcessLogViewModel("Winetricks", newestFirst: true);
        var token = log.Token; // Captured before a closing window disposes its view model.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.PropertyChanged += (_, _) =>
        {
            if (log.LogText.Contains("READY", StringComparison.Ordinal))
                ready.TrySetResult();
        };
        var dispatchLock = new object();
        void Dispatch(Action action) { lock (dispatchLock) action(); }
        var running = vm.ExecuteWinetricksWithLogAsync(Services.WinetricksServiceTests.Request(temp), log, token, Dispatch);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (dispatchLock)
            {
                Assert.True(vm.IsWinetricksRunning);
                Assert.True(log.IsRunning);
                Assert.True(log.CancelCommand.CanExecute(null));
                if (closeWindow)
                    log.Dispose();
                else
                    log.CancelCommand.Execute(null);
            }
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(token.IsCancellationRequested);
            Assert.True(log.IsCancelled);
            Assert.False(vm.IsWinetricksRunning);
            Assert.False(log.IsRunning);
            Assert.False(log.CanCancel);
            Assert.Equal("Cancelled.", log.StatusText);
            Assert.Contains("[CANCELLED]", log.LogText);
            Assert.DoesNotContain("[ERROR]", log.LogText);
            Assert.Empty(errors.Reports);
        }
        finally
        {
            log.Dispose();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static EditMediaViewModel CreateShell(WinetricksService service)
    {
        // Exercise the actual lifecycle method without opening a window or initializing unrelated editor features.
        var vm = (EditMediaViewModel)RuntimeHelpers.GetUninitializedObject(typeof(EditMediaViewModel));
        typeof(EditMediaViewModel).GetField("_winetricksService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, service);
        typeof(EditMediaViewModel).GetField("<RunWinetricksCommand>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new AsyncRelayCommand<Window?>(_ => Task.CompletedTask));
        return vm;
    }
}
