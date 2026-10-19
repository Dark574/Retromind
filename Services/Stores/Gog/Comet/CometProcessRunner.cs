using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed class CometProcessRunner
{
    private const int MaximumCredentialBytes = 16 * 1024;
    private const int MaximumDiagnosticCharacters = 64 * 1024;
    private const string DiagnosticLogFileName = "comet-errors.log";
    private const string ReadinessMarker = "Listening on port 9977";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);

    private readonly CometExecutableResolver _executableResolver;
    private readonly string _dataDirectory;
    private readonly TimeSpan _startupTimeout;

    internal CometProcessRunner(
        CometExecutableResolver executableResolver,
        string dataDirectory)
        : this(executableResolver, dataDirectory, DefaultStartupTimeout)
    {
    }

    internal CometProcessRunner(
        CometExecutableResolver executableResolver,
        string dataDirectory,
        TimeSpan startupTimeout)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (startupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _startupTimeout = startupTimeout;
    }

    internal static string GetDefaultDataDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), "Tools", "Comet", "Data");
    }

    internal async Task<CometProcessSession> StartAsync(
        CometCredentials credentials,
        CancellationToken ct = default)
    {
        ValidateCredentials(credentials);
        var credentialBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        if (credentialBytes.Length > MaximumCredentialBytes)
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
            throw new CometProcessException(
                $"Comet credential input exceeds the {MaximumCredentialBytes}-byte limit.");
        }

        Directory.CreateDirectory(_dataDirectory);
        TryRestrictDirectoryAccess(_dataDirectory);

        var executablePath = _executableResolver.GetRequiredExecutable();
        var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath)
        };
        var diagnosticLog = new CometDiagnosticLog(
            Path.Combine(_dataDirectory, DiagnosticLogFileName),
            MaximumDiagnosticCharacters,
            credentials.AccessToken,
            credentials.RefreshToken,
            credentials.UserId,
            credentials.Username);
        var outputCts = new CancellationTokenSource();
        var readiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        Task exitTask = Task.CompletedTask;

        try
        {
            try
            {
                if (!process.Start())
                    throw new CometProcessException("Comet could not be started.");
            }
            catch (Win32Exception ex)
            {
                throw new CometProcessException("Comet could not be started.", ex);
            }

            stdoutTask = DrainOutputAsync(process.StandardOutput, readiness, outputCts.Token);
            stderrTask = DrainOutputAsync(
                process.StandardError,
                readiness,
                outputCts.Token,
                diagnosticLog.AppendError);
            exitTask = process.WaitForExitAsync();

            await process.StandardInput.BaseStream
                .WriteAsync(credentialBytes.AsMemory(), ct)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();

            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startupCts.CancelAfter(_startupTimeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, startupCts.Token);
            var completed = await Task
                .WhenAny(readiness.Task, exitTask, timeoutTask)
                .ConfigureAwait(false);

            if (completed == readiness.Task && !process.HasExited)
            {
                return new CometProcessSession(
                    process,
                    exitTask,
                    stdoutTask,
                    stderrTask,
                    outputCts,
                    diagnosticLog);
            }

            if (completed == exitTask || process.HasExited)
            {
                throw new CometProcessException(
                    $"Comet exited with code {process.ExitCode} before becoming ready.");
            }

            ct.ThrowIfCancellationRequested();
            throw new CometProcessException(
                $"Comet did not become ready within {_startupTimeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            TryKill(process);
            outputCts.Cancel();
            await ObserveTasksAsync(exitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            outputCts.Dispose();
            diagnosticLog.Dispose();
            process.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }

    private ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var configHome = Path.Combine(_dataDirectory, "config");
        var dataHome = Path.Combine(_dataDirectory, "data");
        var cacheHome = Path.Combine(_dataDirectory, "cache");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = _dataDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--credentials-stdin");
        startInfo.ArgumentList.Add("--quit");
        startInfo.Environment["XDG_CONFIG_HOME"] = configHome;
        startInfo.Environment["XDG_DATA_HOME"] = dataHome;
        startInfo.Environment["XDG_CACHE_HOME"] = cacheHome;
        // Keep post-disconnect synchronization bounded while allowing Comet to
        // shut down normally after a game using the Galaxy SDK exits.
        startInfo.Environment["COMET_IDLE_WAIT"] = "1";
        startInfo.Environment.Remove("VLC_PLUGIN_PATH");
        AppImageToolResolver.ConfigureBundledToolEnvironment(startInfo, executablePath);

        return startInfo;
    }

    private static void ValidateCredentials(CometCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.AccessToken) ||
            string.IsNullOrWhiteSpace(credentials.RefreshToken) ||
            string.IsNullOrWhiteSpace(credentials.UserId) ||
            string.IsNullOrWhiteSpace(credentials.Username))
        {
            throw new ArgumentException("Comet credentials must contain all required values.", nameof(credentials));
        }
    }

    private static async Task DrainOutputAsync(
        StreamReader reader,
        TaskCompletionSource readiness,
        CancellationToken ct,
        Action<string>? lineObserver = null)
    {
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                lineObserver?.Invoke(line);
                if (line.Contains(ReadinessMarker, StringComparison.Ordinal))
                    readiness.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Session shutdown owns cancellation of the output readers.
        }
        catch (IOException)
        {
            // The process can close its pipes while it exits.
        }
    }

    private static void TryRestrictDirectoryAccess(string directory)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best effort for filesystems without Unix permission support.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during startup failure and cancellation.
        }
    }

    private static async Task ObserveTasksAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // The primary startup or shutdown result remains authoritative.
            }
        }
    }

    internal sealed class CometDiagnosticLog : IDisposable
    {
        private readonly object _sync = new();
        private readonly string _path;
        private readonly int _maximumCharacters;
        private readonly string[] _sensitiveValues;
        private StreamWriter? _writer;
        private int _writtenCharacters;

        internal CometDiagnosticLog(
            string path,
            int maximumCharacters,
            params string[] sensitiveValues)
        {
            _path = path;
            _maximumCharacters = maximumCharacters;
            _sensitiveValues = sensitiveValues;
            TryDeleteExistingLog();
        }

        internal void AppendError(string line)
        {
            if (!IsErrorLine(line))
                return;

            lock (_sync)
            {
                if (_writtenCharacters >= _maximumCharacters)
                    return;

                try
                {
                    _writer ??= CreateWriter();
                    var sanitized = Sanitize(line);
                    var remaining = _maximumCharacters - _writtenCharacters;
                    if (sanitized.Length > remaining)
                        sanitized = sanitized[..remaining];

                    _writer.WriteLine(sanitized);
                    _writer.Flush();
                    _writtenCharacters += sanitized.Length + Environment.NewLine.Length;
                }
                catch
                {
                    // Diagnostics must never affect whether a game can launch.
                }
            }
        }

        private static bool IsErrorLine(string line)
        {
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                return true;

            var headerEnd = line.IndexOf(']');
            return headerEnd > 0 &&
                   line.AsSpan(0, headerEnd)
                       .Contains(" ERROR ", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private StreamWriter CreateWriter()
        {
            var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            TryRestrictFileAccess(_path);
            return new StreamWriter(stream, new UTF8Encoding(false));
        }

        private string Sanitize(string line)
        {
            foreach (var value in _sensitiveValues)
            {
                if (!string.IsNullOrEmpty(value))
                    line = line.Replace(value, "<redacted>", StringComparison.Ordinal);
            }

            return line;
        }

        private void TryDeleteExistingLog()
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // A stale diagnostic is preferable to blocking a game launch.
            }
        }

        private static void TryRestrictFileAccess(string path)
        {
            if (!OperatingSystem.IsLinux())
                return;

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort for filesystems without Unix permission support.
            }
        }
    }

    internal sealed class CometProcessSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _exitTask;
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;
        private readonly CancellationTokenSource _outputCts;
        private readonly CometDiagnosticLog _diagnosticLog;
        private int _disposed;

        internal CometProcessSession(
            Process process,
            Task exitTask,
            Task stdoutTask,
            Task stderrTask,
            CancellationTokenSource outputCts,
            CometDiagnosticLog diagnosticLog)
        {
            _process = process;
            _exitTask = exitTask;
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
            _outputCts = outputCts;
            _diagnosticLog = diagnosticLog;
        }

        internal int ProcessId => _process.Id;

        internal bool HasExited => _process.HasExited;

        internal Task WaitForExitAsync(CancellationToken ct = default) =>
            _exitTask.WaitAsync(ct);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            TryKill(_process);
            await ObserveTasksAsync(_exitTask).ConfigureAwait(false);
            _outputCts.Cancel();
            await ObserveTasksAsync(_stdoutTask, _stderrTask).ConfigureAwait(false);
            _outputCts.Dispose();
            _diagnosticLog.Dispose();
            _process.Dispose();
        }
    }
}
