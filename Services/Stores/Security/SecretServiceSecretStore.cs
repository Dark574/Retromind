using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Security;

/// <summary>
/// Linux Secret Service store backed by <c>secret-tool</c> (libsecret / org.freedesktop.secrets).
/// </summary>
public sealed class SecretServiceSecretStore : IPersistentSecretStore
{
    private const string SecretToolExecutable = "secret-tool";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<IEnumerable<SecretToolCandidate>> _resolveCandidates;
    private readonly TimeSpan _commandTimeout;
    private readonly ConcurrentDictionary<SecretKey, SecretStorageStatus> _status = new();
    private static readonly string[] TrustedSecretToolDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/usr/sbin",
        "/sbin",
        "/run/current-system/sw/bin"
    ];

    public SecretServiceSecretStore()
    {
        _resolveCandidates = ResolveSecretToolCandidates;
        _commandTimeout = CommandTimeout;
    }

    // A narrow test seam; production tool discovery still accepts only the existing trusted locations.
    internal SecretServiceSecretStore(string executablePath, TimeSpan? commandTimeout = null)
    {
        _resolveCandidates = () => [new SecretToolCandidate(executablePath, IsBundled: false)];
        _commandTimeout = commandTimeout ?? CommandTimeout;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsLinux())
            return Task.FromResult(false);

        // A dummy lookup can unlock or prompt the desktop wallet. Availability only
        // means that a trusted secret-tool executable exists; real operations handle
        // Secret Service/DBus failures and fall back safely at the composite layer.
        using var candidates = _resolveCandidates().GetEnumerator();
        return Task.FromResult(candidates.MoveNext());
    }

    public SecretStorageStatus GetStorageStatus(SecretKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _status.GetValueOrDefault(key, SecretStorageStatus.Unknown);
    }

    public async Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);
        ct.ThrowIfCancellationRequested();
        try
        {
            await SetCoreAsync(key, secret, ct).ConfigureAwait(false);
            _status[key] = SecretStorageStatus.Persistent;
        }
        catch (OperationCanceledException) { throw; }
        catch { _status[key] = SecretStorageStatus.Failed; throw; }
    }

    private async Task SetCoreAsync(SecretKey key, string secret, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new SecretServiceException("write");

        var args = new[]
        {
            "store",
            $"--label={BuildLabel(key)}",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, secret, ct).ConfigureAwait(false);
        if (!result.Started)
            throw new SecretServiceException("write");

        if (result.ExitCode != 0)
            throw new SecretServiceException("write", result.ExitCode);
    }

    public async Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        try
        {
            var value = await GetCoreAsync(key, ct).ConfigureAwait(false);
            _status[key] = value == null ? SecretStorageStatus.Unknown : SecretStorageStatus.Persistent;
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch { _status[key] = SecretStorageStatus.Failed; throw; }
    }

    private async Task<string?> GetCoreAsync(SecretKey key, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new SecretServiceException("read");

        var args = new[]
        {
            "lookup",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, null, ct).ConfigureAwait(false);
        if (!result.Started)
            throw new SecretServiceException("read");

        if (result.ExitCode == 0)
            return NormalizeSecretValue(result.Stdout);

        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr))
            return null;

        throw new SecretServiceException("read", result.ExitCode);
    }

    public async Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        try
        {
            await DeleteCoreAsync(key, ct).ConfigureAwait(false);
            _status[key] = SecretStorageStatus.Unknown;
        }
        catch (OperationCanceledException) { throw; }
        catch { _status[key] = SecretStorageStatus.Failed; throw; }
    }

    private async Task DeleteCoreAsync(SecretKey key, CancellationToken ct)
    {
        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new SecretServiceException("delete");

        var args = new[]
        {
            "clear",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, null, ct).ConfigureAwait(false);
        if (!result.Started)
            throw new SecretServiceException("delete");

        if (result.ExitCode == 0)
            return;

        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr))
            return;

        throw new SecretServiceException("delete", result.ExitCode);
    }

    private static string BuildLabel(SecretKey key)
        => $"Retromind Secret ({key.Service}/{key.Account})";

    private static string? NormalizeSecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.TrimEnd('\r', '\n');
    }

    private async Task<SecretToolResult> RunSecretToolAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken ct)
    {
        foreach (var candidate in _resolveCandidates())
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunSecretToolWithExecutableAsync(candidate, arguments, standardInput, ct)
                .ConfigureAwait(false);

            if (result.Started)
                return result;
        }

        return new SecretToolResult(
            Started: false,
            ExitCode: -1,
            Stdout: string.Empty,
            Stderr: "secret-tool executable is not available.");
    }

    private async Task<SecretToolResult> RunSecretToolWithExecutableAsync(
        SecretToolCandidate candidate,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = candidate.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            CreateNoWindow = true
        };

        HostProcessEnvironmentSanitizer.Sanitize(startInfo);
        if (candidate.IsBundled)
            AppImageToolResolver.ConfigureBundledToolEnvironment(startInfo, candidate.ExecutablePath);

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
                return new SecretToolResult(false, -1, string.Empty, $"{candidate.ExecutablePath} could not be started.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new SecretToolResult(false, -1, string.Empty, string.Empty);
        }
        catch (Exception)
        {
            throw new SecretServiceException("start");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(_commandTimeout);
        // Drain before sending input so a tool writing output before reading stdin cannot deadlock us.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            if (standardInput != null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new SecretToolResult(true, process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            linkedCts.Cancel();
            TryKill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch { /* Cleanup is bounded even if the OS cannot terminate the command. */ }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch { /* Observe canceled/faulted pipe reads without exposing their payloads. */ }
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (ex is OperationCanceledException)
                throw new TimeoutException("Secret Service command timed out.");
            throw new SecretServiceException("command");
        }
    }

    private static IEnumerable<SecretToolCandidate> ResolveSecretToolCandidates()
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in TrustedSecretToolDirectories)
        {
            var candidate = Path.Combine(directory, SecretToolExecutable);
            if (!File.Exists(candidate))
                continue;

            var normalized = NormalizePathSafe(candidate);
            if (!yielded.Add(normalized))
                continue;

            yield return new SecretToolCandidate(candidate, IsBundled: false);
        }

        var pathResolved = ResolveSecretToolFromTrustedPathSegments();
        if (!string.IsNullOrWhiteSpace(pathResolved))
        {
            var normalizedPathResolved = NormalizePathSafe(pathResolved);
            if (yielded.Add(normalizedPathResolved))
                yield return new SecretToolCandidate(pathResolved, IsBundled: false);
        }

        var bundledPath = AppImageToolResolver.ResolveBundledExecutable(SecretToolExecutable);
        if (string.IsNullOrWhiteSpace(bundledPath))
            yield break;

        var normalizedBundled = NormalizePathSafe(bundledPath);
        if (!yielded.Add(normalizedBundled))
            yield break;

        yield return new SecretToolCandidate(bundledPath, IsBundled: true);
    }

    private static string? ResolveSecretToolFromTrustedPathSegments()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawSegment in segments)
        {
            var normalizedSegment = NormalizePathSafe(rawSegment);
            if (!IsTrustedDirectory(normalizedSegment))
                continue;

            var candidate = Path.Combine(normalizedSegment, SecretToolExecutable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsTrustedDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return false;

        foreach (var trusted in TrustedSecretToolDirectories)
        {
            var normalizedTrusted = NormalizePathSafe(trusted);
            if (string.Equals(directoryPath, normalizedTrusted, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string NormalizePathSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // best-effort
        }
    }

    private readonly record struct SecretToolCandidate(string ExecutablePath, bool IsBundled);
    private readonly record struct SecretToolResult(bool Started, int ExitCode, string Stdout, string Stderr);
}

/// <summary>Safe backend metadata; never retains command output, arguments, secret values or inner exceptions.</summary>
internal sealed class SecretServiceException(string operation, int? exitCode = null)
    : InvalidOperationException($"Secret Service {operation} failed" +
                                (exitCode.HasValue ? $" (exit code {exitCode.Value})." : "."))
{
    internal int? ExitCode { get; } = exitCode;
}
