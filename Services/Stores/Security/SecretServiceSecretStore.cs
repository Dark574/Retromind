using System;
using System.Collections.Generic;
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
public sealed class SecretServiceSecretStore : ISecretStore
{
    private const string SecretToolExecutable = "secret-tool";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AvailabilityCacheTtl = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _availabilityLock = new(1, 1);
    private bool _isAvailableCached;
    private DateTimeOffset _isAvailableCachedUntilUtc = DateTimeOffset.MinValue;
    private static readonly string[] TrustedSecretToolDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/usr/sbin",
        "/sbin",
        "/run/current-system/sw/bin"
    ];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        if (DateTimeOffset.UtcNow < _isAvailableCachedUntilUtc)
            return _isAvailableCached;

        await _availabilityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _isAvailableCachedUntilUtc)
                return _isAvailableCached;

            var probeArgs = new[]
            {
                "lookup",
                "__retromind_probe_key__",
                "__retromind_probe_value__"
            };

            var result = await RunSecretToolAsync(probeArgs, null, ct).ConfigureAwait(false);
            var available = result.Started &&
                            (result.ExitCode == 0 ||
                             (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr)));

            _isAvailableCached = available;
            _isAvailableCachedUntilUtc = DateTimeOffset.UtcNow.Add(AvailabilityCacheTtl);
            return available;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecretStore] Secret Service probe failed: {ex.Message}");
            _isAvailableCached = false;
            _isAvailableCachedUntilUtc = DateTimeOffset.UtcNow.Add(AvailabilityCacheTtl);
            return false;
        }
        finally
        {
            _availabilityLock.Release();
        }
    }

    public async Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Secret Service store is not available.");

        var args = new[]
        {
            "store",
            $"--label={BuildLabel(key)}",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, secret, ct).ConfigureAwait(false);
        if (!result.Started)
            throw new InvalidOperationException("secret-tool executable is not available.");

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Secret Service set failed: {ExtractErrorDetail(result)}");
    }

    public async Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return null;

        var args = new[]
        {
            "lookup",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, null, ct).ConfigureAwait(false);
        if (!result.Started)
            return null;

        if (result.ExitCode == 0)
            return NormalizeSecretValue(result.Stdout);

        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr))
            return null;

        throw new InvalidOperationException($"Secret Service lookup failed: {ExtractErrorDetail(result)}");
    }

    public async Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
            return;

        var args = new[]
        {
            "clear",
            "service", key.Service,
            "account", key.Account
        };

        var result = await RunSecretToolAsync(args, null, ct).ConfigureAwait(false);
        if (!result.Started)
            return;

        if (result.ExitCode == 0)
            return;

        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Stderr))
            return;

        throw new InvalidOperationException($"Secret Service delete failed: {ExtractErrorDetail(result)}");
    }

    private static string BuildLabel(SecretKey key)
        => $"Retromind Secret ({key.Service}/{key.Account})";

    private static string? NormalizeSecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.TrimEnd('\r', '\n');
    }

    private static string ExtractErrorDetail(SecretToolResult result)
    {
        var stderr = result.Stderr?.Trim();
        if (!string.IsNullOrWhiteSpace(stderr))
            return stderr.Length <= 220 ? stderr : stderr[..220] + "...";

        var stdout = result.Stdout?.Trim();
        if (!string.IsNullOrWhiteSpace(stdout))
            return stdout.Length <= 220 ? stdout : stdout[..220] + "...";

        return $"exit code {result.ExitCode}";
    }

    private static async Task<SecretToolResult> RunSecretToolAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken ct)
    {
        foreach (var candidate in ResolveSecretToolCandidates())
        {
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

    private static async Task<SecretToolResult> RunSecretToolWithExecutableAsync(
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
            return new SecretToolResult(false, -1, string.Empty, ex.Message);
        }

        if (standardInput != null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(CommandTimeout);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"secret-tool timed out after {CommandTimeout.TotalSeconds:0} seconds.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new SecretToolResult(true, process.ExitCode, stdout, stderr);
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
