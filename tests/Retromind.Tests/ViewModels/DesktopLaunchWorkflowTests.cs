using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopLaunchWorkflowTests
{
    [Fact]
    public async Task FailedRunnerPreflightLeavesThePlayingTrackAndKeepsExistingDiagnosis()
    {
        using var state = new LaunchState();
        state.Item.RunnerVersionId = "missing";
        await state.Audio.PlayMusicAsync(state.Track);

        Assert.Null(await state.LaunchAsync());

        Assert.Equal([state.Track], state.StartedTracks);
        Assert.Equal(0, state.FirstPlayer.KillCalls);
        Assert.True(state.Logs.HasLog(state.Item.Id));
        Assert.Empty(state.Errors.Reports);
        Assert.False(state.ViewModel.IsLaunchInProgress);
    }

    [Fact]
    public async Task FailedProcessStartRestoresTheExactInterruptedTrack()
    {
        using var state = new LaunchState();
        state.Item.MediaType = MediaType.Emulator;
        state.Item.LauncherPath = state.Temp.GetPath("missing-launcher");
        await state.Audio.PlayMusicAsync(state.Track);

        var result = await state.LaunchAsync();

        Assert.NotNull(result);
        Assert.Equal(LaunchOutcome.StartFailed, result.Outcome);
        Assert.Equal(1, state.FirstPlayer.KillCalls);
        Assert.Equal([state.Track, state.Track], state.StartedTracks);
        Assert.Empty(state.Errors.Reports);
        Assert.False(state.ViewModel.IsLaunchInProgress);
    }

    [Fact]
    public async Task UnexpectedCompletionFailureReportsPhaseAndPreservesTheStartResult()
    {
        using var state = new LaunchState();
        state.ViewModel.ViewLastLaunchLogCommand.CanExecuteChanged += (_, _) =>
            throw new InvalidOperationException("token=private-value");

        var result = await state.LaunchAsync();

        Assert.NotNull(result);
        Assert.True(result.IsStarted);
        var report = Assert.Single(state.Errors.Reports);
        Assert.Equal("Desktop.LaunchWorkflowFailed", report.Code);
        Assert.Null(report.Exception);
        Assert.Equal("Completion", report.Context!["Phase"]);
        Assert.Equal("InvalidOperationException", report.Context["ExceptionType"]);
        Assert.Equal(result.Outcome.ToString(), report.Context["Outcome"]);
        Assert.DoesNotContain("private-value", report.Message + string.Join(" ", report.Context.Values));
        Assert.False(state.ViewModel.IsLaunchInProgress);
    }

    private sealed class LaunchState : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly DesktopContentSession _content;
        public TemporaryDirectory Temp { get; } = new();
        public RecordingErrorService Errors { get; } = new();
        public LaunchLogService Logs { get; }
        public AudioService Audio { get; }
        public string Track { get; }
        public FakePlayer FirstPlayer { get; } = new();
        public List<string> StartedTracks { get; } = new();
        public MainWindowViewModel ViewModel { get; }
        public MediaItem Item { get; }

        public LaunchState()
        {
            AvaloniaTestDispatcher.EnsureStarted();
            Track = Temp.CreateFile("original.mp3");
            Audio = new AudioService(Errors, options =>
            {
                StartedTracks.Add(options.ArgumentList[^1]);
                return StartedTracks.Count == 1 ? FirstPlayer : new FakePlayer();
            });
            Logs = new LaunchLogService(Temp.GetPath("logs"));
            var settings = new AppSettings { EnableSelectionMusicPreview = true };
            _content = new DesktopContentSession(Errors);
            Item = new MediaItem("Fixture") { Files = [new() { Path = Temp.CreateFile("game.sh", "#!/bin/sh\nexit 0\n") }] };
            var node = new MediaNode("Games");
            node.Items.Add(Item);
            ViewModel = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            SetField("_rootItems", new ObservableCollection<MediaNode> { node });
            SetField("_currentSettings", settings);
            SetField("_desktopContent", _content);
            SetField("_bigModeSession", new DesktopBigModeSession(settings, () => null,
                _ => Task.FromResult<LaunchResult?>(null), () => Task.CompletedTask));
            SetField("_audioService", Audio);
            SetField("_errorService", Errors);
            SetField("_launchLogService", Logs);
            SetField("_launcherService", new LauncherService(Temp.RootPath, settings, Logs));
            SetField("_gogWorkflow", RuntimeHelpers.GetUninitializedObject(typeof(DesktopGogWorkflow)));
            SetField("_epicWorkflow", RuntimeHelpers.GetUninitializedObject(typeof(DesktopEpicWorkflow)));
            typeof(MainWindowViewModel).GetMethod("InitializeCommands", PrivateInstance)!.Invoke(ViewModel, null);
        }

        public async Task<LaunchResult?> LaunchAsync()
        {
            LaunchResult? result = null;
            await UiThreadHelper.InvokeAsync(async () =>
            {
                var launch = (Task<LaunchResult?>)typeof(MainWindowViewModel)
                    .GetMethod("PlayMediaAsync", PrivateInstance)!.Invoke(ViewModel, [Item, false])!;
                result = await launch;
            });
            return result;
        }

        private void SetField(string name, object value) =>
            typeof(MainWindowViewModel).GetField(name, PrivateInstance)!.SetValue(ViewModel, value);

        public void Dispose()
        {
            Audio.StopMusic();
            _content.Dispose();
            Temp.Dispose();
        }
    }

    private sealed class FakePlayer : AudioService.IAudioProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int KillCalls { get; private set; }
        public bool HasExited => _exit.Task.IsCompleted;
        public int ExitCode => 137;
        public void Kill()
        {
            KillCalls++;
            _exit.TrySetResult();
        }
        public bool WaitForExit(int milliseconds) => HasExited;
        public Task WaitForExitAsync() => _exit.Task;
        public void Dispose() { }
    }
}
