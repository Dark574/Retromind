using System.Diagnostics;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LaunchProcessServiceTests
{
    [Fact]
    public async Task WatchProcessByNameAsync_ReturnsNotFoundAfterConfiguredTimeout()
    {
        var missingProcessName = "retromind-missing-" + Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        var outcome = await new LaunchProcessService().WatchProcessByNameAsync(
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
        var outcome = await new LaunchProcessService().WatchProcessByNameAsync(
            "already-running",
            wasRunningBeforeLaunch: true,
            startupTimeout: TimeSpan.FromSeconds(30),
            startupPollInterval: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(ProcessWatchOutcome.AlreadyRunning, outcome);
    }

    [Fact]
    public async Task StartAndOutputCapture_PreserveStandardOutputAndError()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var service = new LaunchProcessService();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("printf 'process-output\\n'; printf 'process-error\\n' >&2");

        using var process = service.Start(startInfo);
        Assert.NotNull(process);
        using var capture = service.StartOutputCapture(process);
        Assert.NotNull(capture);

        await process.WaitForExitAsync();
        await capture.WaitForCompletionAsync(TimeSpan.FromSeconds(1));

        var output = capture.GetOutput();
        Assert.Contains("stderr:\nprocess-error", output);
        Assert.Contains("stdout:\nprocess-output", output);
    }

    [Fact]
    public void StartFailure_IncludesCommandAndWorkingDirectory()
    {
        var service = new LaunchProcessService();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/retromind/missing executable",
            WorkingDirectory = "/retromind/missing working directory",
            UseShellExecute = false
        };

        var exception = Assert.Throws<InvalidOperationException>(() => service.Start(startInfo));

        Assert.Contains("Command: \"/retromind/missing executable\"", exception.Message);
        Assert.Contains("Working directory: /retromind/missing working directory", exception.Message);
    }

    [Fact]
    public async Task SessionStop_RequiresASecondRequestBeforeForceKillingDirectProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var service = new LaunchProcessService();
        using var process = Process.Start("/bin/sleep", "30");
        Assert.NotNull(process);
        var registration = service.BeginDirectSession(process);

        try
        {
            var first = await service.RequestActiveSessionStopAsync(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(30));

            Assert.Equal(LaunchSessionStopRequestResult.ForceStopArmed, first);
            Assert.False(process.HasExited);
            Assert.False(service.WasStopRequested(registration));

            var second = await service.RequestActiveSessionStopAsync(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(30));

            Assert.Equal(LaunchSessionStopRequestResult.ForceStopRequested, second);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(service.WasStopRequested(registration));
        }
        finally
        {
            service.EndActiveSession(registration);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task SessionStop_OnlyForceKillsRegisteredWatchedProcessIds()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var processName = "rtm" + Guid.NewGuid().ToString("N")[..8];
        var executablePath = temp.GetPath(processName);
        File.Copy("/bin/sleep", executablePath);
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var process = Process.Start(executablePath, "30");
        using var unrelatedProcess = Process.Start(executablePath, "30");
        Assert.NotNull(process);
        Assert.NotNull(unrelatedProcess);

        var service = new LaunchProcessService();
        var registration = service.BeginWatchedSession(processName, [process.Id]);
        try
        {
            Assert.Equal(
                LaunchSessionStopRequestResult.ForceStopArmed,
                await service.RequestActiveSessionStopAsync(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
            Assert.False(process.HasExited);

            Assert.Equal(
                LaunchSessionStopRequestResult.ForceStopRequested,
                await service.RequestActiveSessionStopAsync(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(service.WasStopRequested(registration));
            Assert.False(unrelatedProcess.HasExited);
        }
        finally
        {
            service.EndActiveSession(registration);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (!unrelatedProcess.HasExited)
                unrelatedProcess.Kill(entireProcessTree: true);
        }
    }
}
