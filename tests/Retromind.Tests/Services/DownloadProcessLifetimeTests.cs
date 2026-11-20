using System.Diagnostics;
using Retromind.Services.Downloads;

namespace Retromind.Tests.Services;

public sealed class DownloadProcessLifetimeTests
{
    [Fact]
    public async Task Worker_ExitsAfterSuccessfulCommandWithParentPipeStillOpen()
    {
        using var worker = StartWorker("printf 'completed\\n'");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal("completed", await worker.StandardOutput.ReadLineAsync(timeout.Token));
        await worker.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, worker.ExitCode);
    }

    [Fact]
    public async Task Worker_StopsTheProcessGroupWhenTheParentPipeCloses()
    {
        using var worker = StartWorker("sleep 30 & printf '%s\\n' \"$!\"; wait");
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
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static Process StartWorker(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Retromind"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            ArgumentList = { DownloadProcessLifetime.WorkerArgument, "/bin/sh", "-c", command }
        };
        return Process.Start(info)!;
    }
}
