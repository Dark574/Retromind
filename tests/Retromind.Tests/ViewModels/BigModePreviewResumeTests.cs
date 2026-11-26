using System.Reflection;
using Avalonia.Threading;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModePreviewResumeTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task FailedResume_ClearsStalePlaybackAndAllowsSameVideoToRestart(int surface, bool throws) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/video.mp4");
            using var fixture = CreatePausedFixture(path, surface);
            // An obsolete incoming surface must also cease to suppress a retry.
            fixture.Set("_mainVideoIsPlayingA", true);
            fixture.Set("_mainVideoIsPlayingB", true);
            fixture.Set("_pendingPreviewVideoPath", path);
            fixture.Set("_pendingPreviewSurfaceIndex", 1 - surface);
            fixture.Set("_pendingPreviewGeneration", 7);
            var visibleDuringPlay = false;
            var overlayVisibleDuringPlay = false;
            var calls = 0;
            bool Play()
            {
                calls++;
                visibleDuringPlay = fixture.Vm.IsVideoVisible;
                overlayVisibleDuringPlay = fixture.Vm.IsVideoOverlayVisible;
                if (throws) throw new InvalidOperationException("Simulated native resume failure");
                return false;
            }

            Assert.False(TryResume(fixture, Play));
            Assert.Equal(1, calls);
            Assert.False(visibleDuringPlay);
            Assert.False(overlayVisibleDuringPlay);
            AssertStopped(fixture);
            fixture.Call("TriggerPreviewPlaybackWithDebounce");
            AssertSameVideoCanRestart(fixture, path);
        });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MissingPausedPlayer_FallsBackToDebouncedStartOfSameVideo(int surface) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/video.mp4");
            using var fixture = CreatePausedFixture(path, surface);
            fixture.Set("_suspendPreviewDuringScroll", true);

            // Exercise the real caller: a missing native player must not leave
            // stale playing flags that cause the debounced retry to be skipped.
            fixture.Call("ResumePreviewAfterScroll");

            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            AssertStopped(fixture);
            AssertSameVideoCanRestart(fixture, path);
        });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SuccessfulResume_RestoresVisibilityAfterPlayWithoutRestarting(int surface) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/video.mp4");
            using var fixture = CreatePausedFixture(path, surface);
            var visibleDuringPlay = false;
            var overlayVisibleDuringPlay = false;
            var calls = 0;
            bool Play()
            {
                calls++;
                visibleDuringPlay = fixture.Vm.IsVideoVisible;
                overlayVisibleDuringPlay = fixture.Vm.IsVideoOverlayVisible;
                return true;
            }

            Assert.True(TryResume(fixture, Play));
            Assert.Equal(1, calls);
            Assert.False(visibleDuringPlay);
            Assert.False(overlayVisibleDuringPlay);
            Assert.True(fixture.Vm.IsVideoVisible);
            Assert.True(fixture.Vm.IsVideoOverlayVisible);
            AssertPauseCleared(fixture);
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            Assert.Equal(surface, fixture.Get<int>("_activePreviewIndex"));
            Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
            Assert.Equal(path, fixture.Get<string?>("_presentedPreviewVideoPath"));
            Assert.Equal(surface == 0, fixture.Get<bool>("_mainVideoIsPlayingA"));
            Assert.Equal(surface == 1, fixture.Get<bool>("_mainVideoIsPlayingB"));
            Assert.Null(fixture.Get<string?>("_pendingPreviewVideoPath"));
        });

    private static BigModeNavigationFixture CreatePausedFixture(string path, int surface)
    {
        var fixture = new BigModeNavigationFixture([new MediaNode("Library")
        {
            Items = [new MediaItem("Video")
                { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/video.mp4" }] }]
        }]);
        fixture.Session.ActivateSelected();
        fixture.Vm.CanShowVideo = true;
        fixture.Set("_isViewReady", true);
        fixture.Set("_previewPlayGeneration", 7);
        fixture.Set("_previewPausedForScroll", true);
        fixture.Set("_pausedPreviewVideoPath", path);
        fixture.Set("_pausedPreviewIndex", surface);
        fixture.Set("_currentPreviewVideoPath", path);
        fixture.Set("_presentedPreviewVideoPath", path);
        fixture.Set("_activePreviewIndex", surface);
        fixture.Set("_mainVideoActiveSurfaceIndex", surface);
        fixture.Set("_mainVideoIsPlayingA", surface == 0);
        fixture.Set("_mainVideoIsPlayingB", surface == 1);
        fixture.Set("_mainVideoHasFrameA", surface == 0);
        fixture.Set("_mainVideoHasFrameB", surface == 1);
        fixture.Vm.IsVideoVisible = false;
        fixture.Vm.IsVideoOverlayVisible = false;
        return fixture;
    }

    private static bool TryResume(BigModeNavigationFixture fixture, Func<bool> play) =>
        (bool)typeof(BigModeViewModel).GetMethod("TryResumePausedPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Vm, [play])!;

    private static void AssertPauseCleared(BigModeNavigationFixture fixture)
    {
        Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
        Assert.Null(fixture.Get<string?>("_pausedPreviewVideoPath"));
        Assert.Equal(-1, fixture.Get<int>("_pausedPreviewIndex"));
    }

    private static void AssertStopped(BigModeNavigationFixture fixture)
    {
        AssertPauseCleared(fixture);
        Assert.False(fixture.Vm.IsVideoVisible);
        Assert.False(fixture.Get<bool>("_mainVideoIsPlayingA"));
        Assert.False(fixture.Get<bool>("_mainVideoIsPlayingB"));
        Assert.False(fixture.Get<bool>("_mainVideoHasFrameA"));
        Assert.False(fixture.Get<bool>("_mainVideoHasFrameB"));
        Assert.Equal(-1, fixture.Get<int>("_activePreviewIndex"));
        Assert.Null(fixture.Get<string?>("_currentPreviewVideoPath"));
        Assert.Null(fixture.Get<string?>("_presentedPreviewVideoPath"));
        Assert.Null(fixture.Get<string?>("_pendingPreviewVideoPath"));
        Assert.Equal(-1, fixture.Get<int>("_pendingPreviewSurfaceIndex"));
    }

    private static void AssertSameVideoCanRestart(BigModeNavigationFixture fixture, string path)
    {
        var generation = fixture.Get<int>("_previewPlayGeneration");
        try
        {
            Assert.True(fixture.Get<DispatcherTimer>("_previewDebounceTimer").IsEnabled);
            fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
            Assert.True(fixture.Get<int>("_previewPlayGeneration") > generation);
            Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
            Assert.Equal(path, fixture.Get<string?>("_pendingPreviewVideoPath"));
        }
        finally
        {
            // Invalidate render continuations before they can call native VLC.
            fixture.Call("StopVideo");
        }
    }
}
