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
    public async Task Close_SavesSelectionAndWaitsForPreviewBeforeDetaching()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseTestTheme(temp);
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = new List<string>();
        BigModeNavigationFixture? fixture = null;
        DesktopBigModeSession? session = null;
        Task? closing = null;

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                fixture = CreateNavigation();
                session = new DesktopBigModeSession(
                    fixture.Settings, () => null, _ => throw new InvalidOperationException("Unexpected launch"),
                    () =>
                    {
                        Assert.Equal(1, fixture.Get<int>("_disposed"));
                        transitions.Add("sync desktop");
                        return Task.CompletedTask;
                    },
                    prepareToDetach: _ =>
                    {
                        transitions.Add("stop preview");
                        stopStarted.SetResult();
                        return previewStopped.Task;
                    });
                session.ContentChanged += () => transitions.Add(session.Content != null ? "attach" : "detach");
                session.Open(_ => fixture.Vm);
                var host = Assert.IsType<BigModeHostView>(session.Content);
                Assert.Same(fixture.Vm, host.DataContext);
                fixture.Session.ActivateSelected();
                closing = fixture.CallAsync("RequestBigModeCloseAsync");
            });
            await stopStarted.Task.WaitAsync(TestTimeout);
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Assert.True(fixture!.Get<bool>("_stateSaved"));
                Assert.Equal(0, fixture.Get<int>("_disposed"));
                Assert.NotNull(session!.Content);
                Assert.Null(session.ActiveViewModel);
                Assert.Equal(["attach", "stop preview"], transitions);

                // Keep the original host until its playback has settled.
                session.Open(_ => throw new InvalidOperationException("Duplicate session"));
            });

            previewStopped.SetResult();
            await closing!.WaitAsync(TestTimeout);
            Assert.Null(session!.Content);
            Assert.Equal(["attach", "stop preview", "detach", "sync desktop"], transitions);
        }
        finally
        {
            previewStopped.TrySetResult();
            if (closing != null)
                await closing.WaitAsync(TestTimeout);
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
                    prepareToDetach: _ => Task.CompletedTask,
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
