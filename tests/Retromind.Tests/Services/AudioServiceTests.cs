using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class AudioServiceTests
{
    [Fact]
    public async Task PlayMusicAsync_RaisesEndedEventWhenPlayerExitsSuccessfully()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        ConfigureFakePlayer(temp, exitCode: 0, out var environment);
        using (environment)
        {
            var musicPath = temp.CreateFile("track.mp3");
            var playbackEnded = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new AudioService();
            service.MusicPlaybackEnded += path => playbackEnded.TrySetResult(path);

            await service.PlayMusicAsync(musicPath);

            Assert.Equal(
                musicPath,
                await playbackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task PlayMusicAsync_DoesNotRaiseEndedEventWhenPlayerFails()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        ConfigureFakePlayer(temp, exitCode: 17, out var environment);
        using (environment)
        {
            var musicPath = temp.CreateFile("broken.mp3");
            var playbackEnded = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new AudioService();
            service.MusicPlaybackEnded += _ => playbackEnded.TrySetResult();

            await service.PlayMusicAsync(musicPath);

            await Assert.ThrowsAsync<TimeoutException>(
                () => playbackEnded.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
        }
    }

    private static void ConfigureFakePlayer(
        TemporaryDirectory temp,
        int exitCode,
        out EnvironmentVariableScope environment)
    {
        var playerPath = temp.CreateFile("ffplay", $"#!/bin/sh\nexit {exitCode}\n");
        File.SetUnixFileMode(
            playerPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var path = string.IsNullOrEmpty(originalPath)
            ? temp.RootPath
            : temp.RootPath + Path.PathSeparator + originalPath;
        environment = new EnvironmentVariableScope(
            ("PATH", path),
            ("APPIMAGE", null),
            ("APPDIR", null));
    }
}
