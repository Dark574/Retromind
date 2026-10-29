using System.ComponentModel;
using System.Reflection;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class AudioServiceTests
{
    [Theory]
    [InlineData("track.mp3", "ffplay")]
    [InlineData("track.SID", "sidplayfp")]
    public async Task PlayMusicAsync_StartFailureReportsWarningWithoutRetryOrNotification(string file, string player)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile(file);
        using var errors = new RecordingErrorService();
        var failure = new Win32Exception("Player unavailable");
        var starts = 0;
        var service = new AudioService(errors, _ => { starts++; throw failure; });
        errors.OnReport = _ => AssertOutsideProcessLock(service);
        var ended = false;
        service.MusicPlaybackEnded += _ => ended = true;

        await service.PlayMusicAsync(path);

        var report = Assert.Single(errors.Reports);
        AssertReport(report, "Audio.StartFailed", ErrorSeverity.Warning, "Start", player);
        Assert.Same(failure, report.Exception);
        Assert.Equal(path, report.Context!["FilePath"]);
        Assert.Equal(1, starts);
        Assert.False(ended);
    }

    [Fact]
    public async Task PlayMusicAsync_NullProcessReportsStartFailure()
    {
        using var temp = new TemporaryDirectory();
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => null);

        await service.PlayMusicAsync(temp.CreateFile("track.mp3"));

        AssertReport(Assert.Single(errors.Reports), "Audio.StartFailed", ErrorSeverity.Warning, "Start", "ffplay");
    }

    [Theory]
    [InlineData("track.mp3", "ffplay")]
    [InlineData("track.sid", "sidplayfp")]
    public async Task PlayMusicAsync_UnexpectedExitReportsWarningWithoutEndedEventOrRetry(string file, string player)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile(file);
        var process = new FakeAudioProcess();
        using var errors = new RecordingErrorService();
        var starts = 0;
        var service = new AudioService(errors, _ => { starts++; return process; });
        errors.OnReport = _ => AssertOutsideProcessLock(service);
        var ended = false;
        service.MusicPlaybackEnded += _ => ended = true;
        await service.PlayMusicAsync(path);

        process.Complete(17);

        var report = Assert.Single(errors.Reports);
        AssertReport(report, "Audio.ProcessFailed", ErrorSeverity.Warning, "Playback", player);
        Assert.Equal("17", report.Context!["ExitCode"]);
        Assert.Equal(path, report.Context["FilePath"]);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(1, starts);
        Assert.False(ended);
    }

    [Fact]
    public async Task PlayMusicAsync_AlreadyExitedPlayerReportsOutsideStartLock()
    {
        using var temp = new TemporaryDirectory();
        var process = new FakeAudioProcess();
        process.Complete(17);
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => process);
        errors.OnReport = _ => AssertOutsideProcessLock(service);

        await service.PlayMusicAsync(temp.CreateFile("track.mp3"));

        AssertReport(Assert.Single(errors.Reports), "Audio.ProcessFailed", ErrorSeverity.Warning, "Playback", "ffplay");
    }

    [Fact]
    public async Task PlayMusicAsync_SuccessRaisesEndedEventWithoutErrorReport()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("track.mp3");
        using var errors = new RecordingErrorService();
        var process = new FakeAudioProcess();
        var service = new AudioService(errors, _ => process);
        var ended = new List<string>();
        service.MusicPlaybackEnded += ended.Add;
        await service.PlayMusicAsync(path);

        process.Complete(0);

        Assert.Equal([path], ended);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task StopMusic_UserStopDoesNotReportKillExitOrRaiseEndedEvent()
    {
        using var temp = new TemporaryDirectory();
        var process = new FakeAudioProcess();
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => process);
        var ended = false;
        service.MusicPlaybackEnded += _ => ended = true;
        await service.PlayMusicAsync(temp.CreateFile("track.mp3"));

        service.StopMusic();
        service.StopMusic();

        Assert.Equal(1, process.KillCalls);
        Assert.Equal(500, process.WaitTimeout);
        Assert.Equal(1, process.DisposeCalls);
        Assert.False(ended);
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData("Kill", "Stop")]
    [InlineData("Wait", "Stop")]
    [InlineData("Dispose", "Dispose")]
    public async Task StopMusic_CleanupFailureReportsDiagnosticAfterDisposalAndOutsideLock(string failureAt, string operation)
    {
        using var temp = new TemporaryDirectory();
        var failure = new InvalidOperationException("Cannot clean up process");
        var process = new FakeAudioProcess
        {
            KillException = failureAt == "Kill" ? failure : null,
            WaitException = failureAt == "Wait" ? failure : null,
            DisposeException = failureAt == "Dispose" ? failure : null
        };
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => process);
        errors.OnReport = _ =>
        {
            AssertOutsideProcessLock(service);
            Assert.Equal(1, process.DisposeCalls);
            Assert.Null(CurrentProcess(service));
        };
        await service.PlayMusicAsync(temp.CreateFile("track.mp3"));

        service.StopMusic();

        var report = Assert.Single(errors.Reports);
        AssertReport(report, "Audio.StopFailed", ErrorSeverity.Diagnostic, operation, "ffplay");
        Assert.Same(failure, report.Exception);
        process.Complete(137); // A delayed exit after failed Kill remains an intentional stop.
        Assert.Single(errors.Reports);
    }

    [Fact]
    public async Task StopMusic_ThrowingErrorServiceDoesNotInterruptCleanupOrNextPlayback()
    {
        using var temp = new TemporaryDirectory();
        var first = new FakeAudioProcess
        {
            KillException = new IOException("Kill failed"),
            DisposeException = new IOException("Dispose failed")
        };
        var second = new FakeAudioProcess();
        var processes = new Queue<FakeAudioProcess>([first, second]);
        using var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new AudioService(errors, _ => processes.Dequeue());
        var path = temp.CreateFile("track.mp3");
        await service.PlayMusicAsync(path);

        service.StopMusic();

        Assert.Equal(1, first.DisposeCalls);
        Assert.Null(CurrentProcess(service));
        Assert.Equal(2, errors.Reports.Count);
        Assert.All(errors.Reports, report => Assert.Equal("Audio.StopFailed", report.Code));
        await service.PlayMusicAsync(path);
        first.Complete(137);
        Assert.Same(second, CurrentProcess(service));
        service.StopMusic();
        Assert.Equal(1, second.DisposeCalls);
        Assert.Equal(2, errors.Reports.Count);
    }

    [Fact]
    public async Task PlayMusicAsync_MonitorCleanupFailureStillAllowsNaturalEndedEvent()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("track.mp3");
        var process = new FakeAudioProcess { DisposeException = new IOException("Dispose failed") };
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => process);
        errors.OnReport = _ => AssertOutsideProcessLock(service);
        var ended = new List<string>();
        service.MusicPlaybackEnded += ended.Add;
        await service.PlayMusicAsync(path);

        process.Complete(0);

        AssertReport(Assert.Single(errors.Reports), "Audio.StopFailed", ErrorSeverity.Diagnostic, "Dispose", "ffplay");
        Assert.Null(CurrentProcess(service));
        Assert.Equal([path], ended);
    }

    [Fact]
    public async Task PlayMusicAsync_ReplacingTrackIgnoresLateExitOfOldPlayback()
    {
        using var temp = new TemporaryDirectory();
        var first = new FakeAudioProcess { CompleteOnKill = false };
        var second = new FakeAudioProcess();
        var processes = new Queue<FakeAudioProcess>([first, second]);
        using var errors = new RecordingErrorService();
        var service = new AudioService(errors, _ => processes.Dequeue());
        var ended = new List<string>();
        service.MusicPlaybackEnded += ended.Add;
        await service.PlayMusicAsync(temp.CreateFile("first.mp3"));
        var secondPath = temp.CreateFile("second.mp3");
        await service.PlayMusicAsync(secondPath);

        first.Complete(17);
        Assert.Same(second, CurrentProcess(service));
        Assert.Empty(ended);
        second.Complete(0);

        Assert.Equal([secondPath], ended);
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.mp3")]
    public async Task PlayMusicAsync_MissingFileStopsCurrentPlaybackWithoutReport(string file)
    {
        using var temp = new TemporaryDirectory();
        var process = new FakeAudioProcess();
        using var errors = new RecordingErrorService();
        var starts = 0;
        var service = new AudioService(errors, _ => { starts++; return process; });
        await service.PlayMusicAsync(temp.CreateFile("track.mp3"));

        await service.PlayMusicAsync(file.Length == 0 ? file : temp.GetPath(file));

        Assert.Equal(1, starts);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Empty(errors.Reports);
    }

    private static void AssertReport(ErrorReport report, string code, ErrorSeverity severity, string operation, string player)
    {
        Assert.Equal(code, report.Code);
        Assert.Equal(severity, report.Severity);
        Assert.Equal(nameof(AudioService), report.Component);
        Assert.False(string.IsNullOrWhiteSpace(report.Message));
        Assert.Equal(operation, report.Context!["Operation"]);
        Assert.Equal(player, report.Context["Player"]);
    }

    private static object? CurrentProcess(AudioService service) => Field(service, "_currentProcess");
    private static object? Field(AudioService service, string name) =>
        typeof(AudioService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

    private static void AssertOutsideProcessLock(AudioService service) =>
        Assert.False(Monitor.IsEntered(Field(service, "_processLock")!), "ErrorService called under _processLock");

    private sealed class RecordingErrorService : IErrorService, IDisposable
    {
        private readonly List<Exception> _assertionFailures = [];
        public List<ErrorReport> Reports { get; } = [];
        public Action<ErrorReport>? OnReport { get; set; }
        public bool ThrowOnReport { get; init; }
        public void Report(ErrorReport error, Func<Task>? notifyUser = null)
        {
            Reports.Add(error);
            // The caller intentionally catches errors from IErrorService. Preserve test
            // assertions for verification outside that boundary so they cannot be swallowed.
            var assertionFailure = Record.Exception(() =>
            {
                Assert.Null(notifyUser);
                OnReport?.Invoke(error);
            });
            if (assertionFailure != null)
                _assertionFailures.Add(assertionFailure);
            if (ThrowOnReport)
                throw new IOException("Error service unavailable");
        }

        public void Dispose() => Assert.Empty(_assertionFailures);
    }

    private sealed class FakeAudioProcess : AudioService.IAudioProcess
    {
        // Inline completion deliberately exercises exit callbacks occurring during Kill
        // while the service holds its lock, without real processes, delays, or timers.
        private readonly TaskCompletionSource _exit = new();
        public bool HasExited => _exit.Task.IsCompleted;
        public int ExitCode { get; private set; }
        public int KillCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int? WaitTimeout { get; private set; }
        public bool CompleteOnKill { get; init; } = true;
        public Exception? KillException { get; init; }
        public Exception? WaitException { get; init; }
        public Exception? DisposeException { get; init; }

        public void Complete(int exitCode)
        {
            ExitCode = exitCode;
            _exit.TrySetResult();
        }

        public void Kill()
        {
            KillCalls++;
            if (KillException != null)
                throw KillException;
            if (CompleteOnKill)
                Complete(137);
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitTimeout = milliseconds;
            if (WaitException != null)
                throw WaitException;
            return HasExited;
        }

        public Task WaitForExitAsync() => _exit.Task;
        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeException != null)
                throw DisposeException;
        }
    }
}
