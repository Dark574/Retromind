using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Downloads;

/// <summary>
/// Runs a noninteractive Legendary update in its own Linux process group. The
/// Retromind process keeps its stdin pipe open for the entire operation.
/// </summary>
internal static partial class DownloadProcessLifetime
{
    internal const string WorkerArgument = "--retromind-download-child";

    internal static bool UseParentPipe(ProcessStartInfo startInfo)
    {
        // Test runners or other hosts cannot re-enter Retromind's program.
        if (Assembly.GetEntryAssembly() != typeof(Program).Assembly)
            return false;

        var host = Environment.ProcessPath ?? throw new InvalidOperationException("The Retromind executable is unavailable.");
        var target = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.FileName = host;
        startInfo.ArgumentList.Clear();
        if (Path.GetFileNameWithoutExtension(host) == "dotnet")
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(WorkerArgument);
        startInfo.ArgumentList.Add(target);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.RedirectStandardInput = true;
        return true;
    }

    internal static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length < 2 || NewSession() < 0)
            return 125;

        using var parentPipe = Console.OpenStandardInput();
        var parentEnded = WaitForParentPipeAsync(parentPipe);
        if (parentEnded.IsCompleted)
            return 125;

        var info = new ProcessStartInfo(args[1])
        {
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        foreach (var argument in args.Skip(2))
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process == null)
            return 125;
        process.StandardInput.Close();

        var childExited = process.WaitForExitAsync();
        await Task.WhenAny(childExited, parentEnded).ConfigureAwait(false);
        if (parentEnded.IsCompleted)
        {
            // Close the whole private group, including multiprocessing workers.
            Kill(0, 9);
            return 125;
        }
        await childExited.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            // On a backend failure, subprocesses may still hold inherited pipes.
            Kill(0, 9);
        }

        return process.ExitCode;
    }

    private static async Task WaitForParentPipeAsync(Stream pipe)
    {
        try
        {
            await pipe.ReadAsync(new byte[1], CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException) { /* A broken pipe also means the owner is gone. */ }
        catch (ObjectDisposedException) { /* The backend finished first. */ }
    }

    [LibraryImport("libc", EntryPoint = "setsid")]
    private static partial int NewSession();

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int Kill(int processGroup, int signal);
}
