using System.Diagnostics;
using Retromind.Services.Downloads;

namespace Retromind.Tests.Services;

public sealed class DownloadProcessLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_ExitsAfterSuccessfulCommandWithParentPipeStillOpen(bool managedDll)
    {
        using var worker = StartWorker("printf 'completed\\n'", managedDll);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal("completed", await worker.StandardOutput.ReadLineAsync(timeout.Token));
        await worker.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, worker.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_StopsTheProcessGroupWhenTheParentPipeCloses(bool managedDll)
    {
        using var worker = StartWorker("sleep 30 & printf '%s\\n' \"$!\"; wait", managedDll);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = await worker.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.True(int.TryParse(output, out var childPid));

        worker.StandardInput.Close();

        await worker.WaitForExitAsync(timeout.Token);
        Assert.NotEqual(0, worker.ExitCode);
        var statusPath = $"/proc/{childPid}/stat";
        while (true)
        {
            try
            {
                var status = await File.ReadAllTextAsync(statusPath, timeout.Token);
                if (status[status.LastIndexOf(')') + 2] == 'Z')
                    break;
            }
            // Linux can return ESRCH when the process vanishes after /proc/stat was opened.
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException || (ex.HResult & 0xFFFF) == 3)
            {
                break;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static Process StartWorker(string command, bool managedDll)
    {
        var info = new ProcessStartInfo
        {
            FileName = managedDll ? "dotnet" : Path.Combine(AppContext.BaseDirectory, "Retromind"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (managedDll) info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Retromind.dll"));
        foreach (var argument in new[] { DownloadProcessLifetime.WorkerArgument, "/bin/sh", "-c", command })
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
}
