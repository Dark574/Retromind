using System.Runtime.InteropServices;
using Retromind.Helpers.Video;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Retromind.Views;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopBigModeSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Close_WaitsForNativeTeardownOffTheUiThreadBeforeTheNextSession()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseTestTheme(temp);
        var freeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = new List<string>();
        BigModeNavigationFixture? first = null;
        BigModeNavigationFixture? second = null;
        DesktopBigModeSession? session = null;
        Task? closing = null;
        Task? shutdown = null;

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                first = CreateNavigation();
                var surface = new LibVlcVideoSurface(null, 0, Marshal.AllocHGlobal, pointer =>
                {
                    freeStarted.SetResult();
                    allowFree.Task.GetAwaiter().GetResult();
                    Marshal.FreeHGlobal(pointer);
                });
                var chroma = Marshal.AllocHGlobal(4);
                try
                {
                    IntPtr opaque = IntPtr.Zero;
                    uint width = 16, height = 16, pitch = 0, lines = 0;
                    Assert.Equal(1U, surface.VideoFormat(ref opaque, chroma, ref width, ref height, ref pitch, ref lines));
                }
                finally { Marshal.FreeHGlobal(chroma); }
                first.Set("_videoSurfaceA", surface);

                session = new DesktopBigModeSession(
                    first.Settings, () => null, _ => throw new InvalidOperationException("Unexpected launch"),
                    () =>
                    {
                        Assert.True(first.Vm.VideoTeardown.IsCompletedSuccessfully);
                        transitions.Add("sync desktop");
                        return Task.CompletedTask;
                    });
                session.ContentChanged += () => transitions.Add(session.Content != null ? "attach" : "detach");
                session.Open(_ => first.Vm);
                first.Session.ActivateSelected();
                Assert.NotNull(first.Vm.SelectedItem);
                first.Get<System.Collections.ObjectModel.ObservableCollection<MediaNode>>("_rootNodes")[0].Items.Clear();
                closing = first.CallAsync("RequestBigModeCloseAsync");
            });

            await freeStarted.Task.WaitAsync(TestTimeout);
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Assert.True(first!.Get<bool>("_stateSaved"));
                Assert.NotNull(first.Settings.BigModeNavigation);
                Assert.Null(first.Settings.BigModeNavigation.SelectedMediaId);
                Assert.False(first.Vm.VideoTeardown.IsCompleted);
                Assert.NotNull(session!.Content);
                Assert.Null(session.ActiveViewModel);
                Assert.Equal(["attach"], transitions);
                session.Open(_ => throw new InvalidOperationException("Duplicate session"));
                shutdown = session.ShutdownAsync();
                Assert.False(shutdown.IsCompleted);
            });

            allowFree.SetResult();
            await Task.WhenAll(closing!, shutdown!).WaitAsync(TestTimeout);
            Assert.Equal(["attach", "detach", "sync desktop"], transitions);

            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                second = CreateNavigation();
                session!.Open(_ => second.Vm);
                Assert.Same(second.Vm, session.ActiveViewModel);
            });
            await session!.ShutdownAsync().WaitAsync(TestTimeout);
            Assert.Null(session.Content);
            Assert.True(second!.Vm.VideoTeardown.IsCompletedSuccessfully);
        }
        finally
        {
            allowFree.TrySetResult();
            if (session != null)
                await session.ShutdownAsync().WaitAsync(TestTimeout);
            foreach (var fixture in new[] { first, second })
                if (fixture != null)
                    await AvaloniaTestDispatcher.InvokeAsync(_ => DisposeFixtureIfNeeded(fixture));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Play_RestoresFocusOnlyAfterATrackedGameInTheCurrentSession(bool tracked, bool closeBeforeReturn)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseTestTheme(temp);
        var gameResult = new TaskCompletionSource<LaunchResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new MediaItem("Game");
        BigModeNavigationFixture? fixture = null;
        BigModeHostView? host = null;
        BigModeHostView? focusedHost = null;
        Task? playing = null;

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                fixture = CreateNavigation();
                var session = new DesktopBigModeSession(
                    fixture.Settings, () => null, launchedItem =>
                    {
                        Assert.Same(item, launchedItem);
                        return gameResult.Task;
                    },
                    () => Task.CompletedTask,
                    restoreGameFocus: view => focusedHost = view);
                session.Open(_ => fixture.Vm);
                host = session.Content;
                playing = fixture.Get<Func<MediaItem, Task>>("RequestPlay")(item);
                Assert.NotNull(host);
                Assert.Null(focusedHost);
            });

            if (closeBeforeReturn)
                await UiThreadHelper.InvokeAsync(() => fixture!.CallAsync("RequestBigModeCloseAsync"));

            gameResult.SetResult(tracked ? LaunchResult.TrackedSessionCompleted : LaunchResult.Started);
            await playing!.WaitAsync(TestTimeout);

            Assert.Same(tracked && !closeBeforeReturn ? host : null, focusedHost);
        }
        finally
        {
            gameResult.TrySetResult(LaunchResult.Started);
            if (playing != null)
                await playing.WaitAsync(TestTimeout);
            if (fixture != null)
            {
                await UiThreadHelper.InvokeAsync(async () =>
                {
                    if (fixture.Get<int>("_disposed") == 0)
                        await fixture.CallAsync("RequestBigModeCloseAsync");
                    DisposeFixtureIfNeeded(fixture);
                });
            }
        }
    }

    private static EnvironmentVariableScope UseTestTheme(TemporaryDirectory temp)
    {
        temp.CreateFile(Path.Combine("Themes", ThemeDiscovery.DefaultThemePath),
            """<Border xmlns="https://github.com/avaloniaui" />""");
        return new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
    }

    private static BigModeNavigationFixture CreateNavigation()
    {
        ManagedCursorFactory.EnsureRegistered();
        var fixture = new BigModeNavigationFixture([new MediaNode("Category") { Items = [new MediaItem("Game")] }]);
        fixture.Set("_gamepadService", new GamepadService(fixture.Settings, errorService: new RecordingErrorService()));
        return fixture;
    }

    private static void DisposeFixtureIfNeeded(BigModeNavigationFixture fixture)
    {
        if (fixture.Get<int>("_disposed") == 0)
            fixture.Dispose();
    }
}
