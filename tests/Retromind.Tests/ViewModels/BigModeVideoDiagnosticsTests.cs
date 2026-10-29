using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibVLCSharp.Shared;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeVideoDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PlaybackFailure_ReportsStructuredWarningWithoutNotificationAndDeduplicatesAttempt(int index)
    {
        var errors = new RecordingErrorService();
        var vm = CreateShell(errors);
        Begin(vm, index, "preview.mp4", 42);
        var exception = new IOException("Decoder failed");

        await ReportPlaybackAsync(vm, index, "Play", exception);
        await ReportPlaybackAsync(vm, index, "EncounteredError");

        var report = Assert.Single(errors.Reports);
        Assert.Equal("Video.PlaybackFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal(nameof(BigModeViewModel), report.Component);
        Assert.Equal("Play", report.Context!["Operation"]);
        Assert.Equal(index.ToString(), report.Context["SurfaceIndex"]);
        Assert.Equal("preview.mp4", report.Context["VideoPath"]);
        Assert.Same(exception, report.Exception);
        Assert.All(errors.Notifications, Assert.Null);

        SetField(vm, "_previewPlayGeneration", 43);
        Begin(vm, index, "next.mp4", 43);
        await ReportPlaybackAsync(vm, index, "Play", generation: 42);
        Assert.Single(errors.Reports);
        await ReportPlaybackAsync(vm, index, "Play", generation: 43);
        Assert.Equal(2, errors.Reports.Count);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("ThemeSwap")]
    [InlineData("GenerationChanged")]
    [InlineData("Launch")]
    [InlineData("Dispose")]
    [InlineData("Cancellation")]
    [InlineData("Preparing")]
    public async Task PlaybackFailure_ExpectedTransitionsDoNotReport(string transition)
    {
        var errors = new RecordingErrorService();
        var vm = CreateShell(errors);
        Begin(vm, 0, "preview.mp4", 42);
        Exception? exception = null;
        switch (transition)
        {
            case "Stop":
                Invoke(vm, "StopPreviewPlayer", 0); // No player is required to exercise the actual stop guard.
                break;
            case "ThemeSwap": SetField(vm, "_isViewReady", false); break;
            case "GenerationChanged": SetField(vm, "_previewPlayGeneration", 43); break;
            case "Launch": SetField(vm, "_isLaunching", true); break;
            case "Dispose": SetField(vm, "_disposed", 1); break;
            case "Cancellation": exception = new OperationCanceledException(); break;
            case "Preparing": Begin(vm, 0, "preview.mp4", 42, playbackExpected: false); break;
        }

        await ReportPlaybackAsync(vm, 0, "EncounteredError", exception);

        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task PlaybackFailure_SecondaryExpectedStopAndReplacedPlayerDoNotReport()
    {
        var errors = new RecordingErrorService();
        var vm = CreateShell(errors);
        Begin(vm, 2, "background.mp4", 1);
        Invoke(vm, "ExpectVideoStop", 2);
        await ReportPlaybackAsync(vm, 2, "EncounteredError");
        Assert.Empty(errors.Reports);

        var oldPlayer = NativeShell<MediaPlayer>();
        var currentPlayer = NativeShell<MediaPlayer>();
        Begin(vm, 0, "preview.mp4", 42);
        SetField(vm, "_mediaPlayerA", currentPlayer);
        await ReportPlaybackAsync(vm, 0, "EncounteredError", expectedPlayer: oldPlayer);
        Assert.Empty(errors.Reports);
        await ReportPlaybackAsync(vm, 0, "EncounteredError", expectedPlayer: currentPlayer);
        Assert.Single(errors.Reports);
    }

    [Theory]
    [InlineData("Video.StopFailed", "StopPreview")]
    [InlineData("Video.ResourceCleanupFailed", "DisposePreviewMedia")]
    public async Task CleanupFailure_ReportsDiagnosticEvenDuringShutdown(string code, string operation)
    {
        var errors = new RecordingErrorService();
        var vm = CreateShell(errors);
        Begin(vm, 1, "preview.mp4", 42);
        SetField(vm, "_disposed", 1);
        var exception = new IOException("Cleanup failed");

        await (Task)Invoke(vm, "ReportVideoIssueAsync", code, operation, 1, exception)!;

        var report = Assert.Single(errors.Reports);
        Assert.Equal(code, report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
        Assert.Equal("1", report.Context!["SurfaceIndex"]);
        Assert.Equal("preview.mp4", report.Context["VideoPath"]);
        Assert.Same(exception, report.Exception);
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task PlaybackAndCleanupDiagnostics_ContainThrowingErrorService()
    {
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var vm = CreateShell(errors);
        Begin(vm, 0, "preview.mp4", 42);

        await ReportPlaybackAsync(vm, 0, "Play");
        await (Task)Invoke(vm, "ReportVideoIssueAsync", "Video.StopFailed", "StopPreview", 0,
            new IOException("Stop failed"))!;

        Assert.Equal(2, errors.Reports.Count);
    }

    [Fact]
    public async Task SurfaceFailure_ReachesCentralServiceWithNativeDimensionContext()
    {
        var errors = new RecordingErrorService();
        var vm = CreateShell(errors);
        var surface = (LibVlcVideoSurface)Field(vm, "_videoSurfaceA")!;
        Begin(vm, 0, "preview.mp4", 42);
        var report = VideoDiagnostics.CreateReport(nameof(LibVlcVideoSurface), "Video.PlaybackFailed",
            "VideoFormat", 0, surface.VideoPath, new ArgumentOutOfRangeException("width"),
            new Dictionary<string, string> { ["Width"] = "4294967295", ["Height"] = "1080" });

        Invoke(vm, "OnVideoSurfaceError", 0, report);
        await errors.FirstReport.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(report, Assert.Single(errors.Reports));
        Assert.Equal("4294967295", report.Context!["Width"]);
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task VideoDiagnostics_SlowWriterDoesNotBlockCallingThread()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new RecordingErrorService
        {
            OnReport = _ => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        };
        var report = VideoDiagnostics.CreateReport("Test", "Video.PlaybackFailed", "Play", 0);
        var reporting = VideoDiagnostics.ReportAsync(errors, report);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reporting.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await reporting;
        }
        Assert.Same(report, Assert.Single(errors.Reports));
    }

    [Fact]
    public void MarkPreviewMediaForDispose_RapidSurfaceReuseRetainsEveryPendingMedia()
    {
        var vm = CreateShell(new RecordingErrorService());
        var first = NativeShell<Media>();
        var second = NativeShell<Media>();
        var otherSurface = NativeShell<Media>();

        SetField(vm, "_currentPreviewMediaA", first);
        Invoke(vm, "MarkPreviewMediaForDispose", 0);
        // No Stopped event has arrived before the same surface receives and queues another Media.
        SetField(vm, "_currentPreviewMediaA", second);
        Invoke(vm, "MarkPreviewMediaForDispose", 0);
        SetField(vm, "_currentPreviewMediaB", otherSurface);
        Invoke(vm, "MarkPreviewMediaForDispose", 1);
        Invoke(vm, "MarkPreviewMediaForDispose", 0); // An already-queued media must not be queued twice.

        var pending = (Dictionary<int, List<Media>>)Field(vm, "_pendingDisposeMedia")!;
        Assert.Equal([first, second], pending[0]);
        Assert.Equal([otherSurface], pending[1]);
        Assert.Null(Field(vm, "_currentPreviewMediaA"));
        Assert.Null(Field(vm, "_currentPreviewMediaB"));
        // Only references are tested. These shells never own native VLC handles or get disposed.
    }

    private static BigModeViewModel CreateShell(IErrorService errors)
    {
        // Avoid constructing LibVLC, SDL, or an Avalonia window. Only diagnostic state is exercised.
        var vm = (BigModeViewModel)RuntimeHelpers.GetUninitializedObject(typeof(BigModeViewModel));
        SetField(vm, "_errorService", errors);
        SetField(vm, "_videoPlaybackExpected", new int[3]);
        SetField(vm, "_videoPlaybackGenerations", new int[3]);
        SetField(vm, "_videoPlaybackFailureReported", new int[3]);
        SetField(vm, "_videoDiagnosticsLock", Activator.CreateInstance(typeof(Lock))!);
        SetField(vm, "_previewMediaLock", Activator.CreateInstance(typeof(Lock))!);
        SetField(vm, "_pendingDisposeMedia", new Dictionary<int, List<Media>>());
        SetField(vm, "_videoSurfaceA", new LibVlcVideoSurface());
        SetField(vm, "_videoSurfaceB", new LibVlcVideoSurface());
        SetField(vm, "_secondaryVideoSurface", new LibVlcVideoSurface());
        SetField(vm, "_isViewReady", true);
        SetField(vm, "_previewPlayGeneration", 42);
        SetField(vm, "_activePreviewIndex", -1);
        return vm;
    }

    private static T NativeShell<T>() where T : class
    {
        var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(value);
        return value;
    }

    private static Task ReportPlaybackAsync(BigModeViewModel vm, int index, string operation,
        Exception? exception = null, int? generation = null, MediaPlayer? expectedPlayer = null) =>
        (Task)Invoke(vm, "ReportVideoPlaybackFailureAsync", index, operation, exception, generation, expectedPlayer, true, null)!;

    private static void Begin(BigModeViewModel vm, int index, string? path, int generation, bool playbackExpected = true) =>
        Invoke(vm, "BeginVideoPlayback", index, path, generation, playbackExpected);

    private static object? Invoke(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Field(object target, string field) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private sealed class RecordingErrorService : IErrorService
    {
        public ConcurrentQueue<ErrorReport> Reports { get; } = new();
        public ConcurrentQueue<Func<Task>?> Notifications { get; } = new();
        public TaskCompletionSource FirstReport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<ErrorReport>? OnReport { get; init; }
        public bool ThrowOnReport { get; init; }
        public void Report(ErrorReport error, Func<Task>? notifyUser = null)
        {
            Reports.Enqueue(error);
            Notifications.Enqueue(notifyUser);
            OnReport?.Invoke(error);
            FirstReport.TrySetResult();
            if (ThrowOnReport)
                throw new IOException("Diagnostic output failed");
        }
    }
}
