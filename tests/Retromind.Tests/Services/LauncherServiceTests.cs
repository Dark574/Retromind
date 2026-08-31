using System.Diagnostics;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LauncherServiceTests
{
    [Fact]
    public async Task LaunchAsync_ReturnsFailureWhenExecutableDoesNotExist()
    {
        using var temp = new TemporaryDirectory();
        var missingExecutable = temp.GetPath("missing-game-executable");
        var item = new MediaItem("Missing Game")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = missingExecutable
                }
            ]
        };
        var service = new LauncherService(temp.RootPath, new AppSettings());

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.False(result.IsStarted);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Contains(missingExecutable, result.ErrorMessage);
        Assert.Equal(0, item.PlayCount);
        Assert.Null(item.LastPlayed);
    }

    [Fact]
    public async Task LaunchAsync_ReturnsCapturedOutputWhenProcessExitsEarlyWithError()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile(
            "failing-launch.sh",
            "#!/bin/sh\nprintf 'helpful launcher error\\n' >&2\ni=0\nwhile [ \"$i\" -lt 300 ]; do\n  printf 'diagnostic-line-%03d-xxxxxxxxxxxxxxxx\\n' \"$i\"\n  i=$((i + 1))\ndone\nprintf 'launcher output\\n'\nexit 23\n");
        var item = new MediaItem("Failing Launcher")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var service = new LauncherService(temp.RootPath, new AppSettings());

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("helpful launcher error", result.ConsoleOutput);
        Assert.Contains("launcher output", result.ConsoleOutput);
        Assert.True(result.ConsoleOutput!.Length <= 4100);
    }

    [Fact]
    public async Task WatchProcessByNameAsync_ReturnsNotFoundAfterConfiguredTimeout()
    {
        var missingProcessName = "retromind-missing-" + Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await LauncherService.WatchProcessByNameAsync(
            missingProcessName,
            wasRunningBeforeLaunch: false,
            startupTimeout: TimeSpan.FromMilliseconds(50),
            startupPollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(ProcessWatchOutcome.NotFound, outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WatchProcessByNameAsync_DoesNotWaitForProcessThatWasAlreadyRunning()
    {
        var outcome = await LauncherService.WatchProcessByNameAsync(
            "already-running",
            wasRunningBeforeLaunch: true,
            startupTimeout: TimeSpan.FromSeconds(30),
            startupPollInterval: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(ProcessWatchOutcome.AlreadyRunning, outcome);
    }
}
