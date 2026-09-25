using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Stores one replaceable launch log per media item outside the serialized library.
/// Logging is best-effort and must never affect whether a game can be launched.
/// </summary>
public sealed class LaunchLogService
{
    private readonly string _logDirectory;

    public LaunchLogService(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = Path.GetFullPath(logDirectory);
    }

    public bool HasLog(string? mediaItemId)
    {
        var path = GetLogPath(mediaItemId);
        return path != null && File.Exists(path);
    }

    public async Task<string?> TryReadAsync(string? mediaItemId)
    {
        var path = GetLogPath(mediaItemId);
        if (path == null || !File.Exists(path))
            return null;

        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LaunchLog] Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    public async Task TryWriteAsync(string? mediaItemId, string content)
    {
        var path = GetLogPath(mediaItemId);
        if (path == null)
            return;

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false))
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LaunchLog] Could not write '{path}': {ex.Message}");
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    public Task TryWritePreflightFailureAsync(
        MediaItem item,
        EmulatorConfig? emulator,
        AppSettings settings,
        bool recordsStatistics,
        string errorMessage)
    {
        var log = new LaunchLogBuilder(
            item,
            emulator,
            settings,
            wrappers: null,
            recordsStatistics);
        return TryWriteAsync(
            item.Id,
            log.Build(
                "Pre-launch validation failed",
                TimeSpan.Zero,
                errorMessage: errorMessage));
    }

    public void TryDelete(string? mediaItemId)
    {
        var path = GetLogPath(mediaItemId);
        if (path == null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LaunchLog] Could not delete '{path}': {ex.Message}");
        }
    }

    internal string? GetLogPath(string? mediaItemId)
    {
        if (string.IsNullOrWhiteSpace(mediaItemId))
            return null;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mediaItemId));
        var fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".log";
        return Path.Combine(_logDirectory, fileName);
    }
}
