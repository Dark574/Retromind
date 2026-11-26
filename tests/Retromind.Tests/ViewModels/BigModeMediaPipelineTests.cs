using System.Diagnostics;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeMediaPipelineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CircularPreloads_FollowSelectionAndEndAtContextBoundaries() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var root = new MediaNode("Library") { Children = [new MediaNode("Category")],
                Items = new(Enumerable.Range(0, 40).Select(i => new MediaItem($"Media {i}"))) };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1);
            fixture.Vm.CircularLogoPreloadCount = 2;
            _ = fixture.Vm.CircularItems;
            var first = fixture.Get<CancellationTokenSource>("_circularLogoPreloadCts").Token;
            fixture.Vm.SelectNextCommand.Execute(null);
            Assert.True(first.IsCancellationRequested);
            var second = fixture.Get<CancellationTokenSource>("_circularLogoPreloadCts").Token;
            Assert.False(second.IsCancellationRequested);
            fixture.Session.TrySelect(0);
            Assert.True(second.IsCancellationRequested);
            Assert.Null(fixture.Get<CancellationTokenSource?>("_circularLogoPreloadCts"));
            fixture.Session.TrySelect(1);
            var third = fixture.Get<CancellationTokenSource>("_circularLogoPreloadCts").Token;
            fixture.Vm.ResetThemeWindowUsage();
            Assert.True(third.IsCancellationRequested);
            _ = fixture.Vm.CircularItems;
            var fourth = fixture.Get<CancellationTokenSource>("_circularLogoPreloadCts").Token;
            fixture.Vm.NotifyNavigationInputLost();
            Assert.True(fourth.IsCancellationRequested);
        });

    [Fact]
    public async Task VideoResolution_RechecksAtPlaybackAndInvalidatesUnselectedEdits() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var first = new MediaItem("First")
                { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/first.mp4" }] };
            var second = new MediaItem("Second");
            var root = new MediaNode("Library") { Items = [first, second], VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            string? Resolve() => (string?)typeof(Retromind.ViewModels.BigModeViewModel)
                .GetMethod("ResolvePreviewVideoPath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(fixture.Vm, null);
            Assert.Null(Resolve());
            temp.CreateFile("Library/first.mp4");
            Assert.Null(Resolve()); // Shared negative result within this selection.
            fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
            Assert.Equal(temp.GetPath("Library/first.mp4"), Resolve());

            fixture.Session.TrySelect(1);
            first.Assets = [new() { Type = AssetType.Video, RelativePath = "Library/edited.mp4" }];
            temp.CreateFile("Library/edited.mp4");
            fixture.Session.TrySelect(0);
            Assert.Equal(temp.GetPath("Library/edited.mp4"), Resolve());

            fixture.Session.TrySelect(1);
            temp.CreateFile("Library/new-fallback.mp4");
            root.Assets = [new() { Type = AssetType.Video, RelativePath = "Library/new-fallback.mp4" }];
            dispatcher.RunJobs();
            Assert.Equal(temp.GetPath("Library/new-fallback.mp4"), Resolve());
            fixture.Vm.InvalidatePreviewCaches();
            Assert.Equal(temp.GetPath("Library/new-fallback.mp4"), Resolve());

            // A replaced launch file invalidates a previously missing sibling video.
            second.Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = "Library/game.rom" }];
            temp.CreateFile("Library/game.rom");
            temp.CreateFile("Library/game.mp4");
            dispatcher.RunJobs();
            Assert.Equal(temp.GetPath("Library/game.mp4"), Resolve());
            Assert.Equal("Library/game.rom", second.Files[0].Path);
        });

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task VideoProbeWorkload(bool fallback, int simulatedProbeMilliseconds) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var root = new MediaNode("Large library")
            {
                VideoFallbackEnabled = fallback,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }],
                Items = new(Enumerable.Range(0, 10_000).Select(i => new MediaItem($"Media {i}")
                {
                    Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = $"Library/media-{i}.rom" }]
                }))
            };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Vm.CanShowVideo = true;
            var probes = 0;
            fixture.Vm.PreviewFileExistsOverride = path =>
            {
                probes++;
                if (simulatedProbeMilliseconds > 0) Thread.Sleep(simulatedProbeMilliseconds);
                return path.EndsWith(".rom", StringComparison.Ordinal) || path.EndsWith("fallback.mp4", StringComparison.Ordinal);
            };
            fixture.Vm.InvalidatePreviewCaches();
            fixture.Session.ActivateSelected();
            // Warm JIT, node fallback and fixed setup outside the measured burst.
            fixture.Session.TrySelect(1);
            Assert.Equal(fallback ? temp.GetPath("Library/fallback.mp4") : null, fixture.Get<string?>("_selectionVideoPath"));
            probes = 0;
            var count = simulatedProbeMilliseconds == 0 ? 2000 : 100;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                fixture.Vm.SelectNextCommand.Execute(null);
                fixture.Vm.GetPreviewPresentationState(); // Host presentation check for the same selection.
            }
            watch.Stop();
            output.WriteLine($"10,000 items; {count} selections; fallback={fallback}; simulated probe={simulatedProbeMilliseconds}ms; " +
                $"probes={probes}; elapsed={watch.Elapsed.TotalMilliseconds:F1}ms; allocated={GC.GetAllocatedBytesForCurrentThread() - allocated}B");
            Assert.Equal(count * 10, probes);
        });
}
