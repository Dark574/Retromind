using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Retromind.Services.Stores;

namespace Retromind.Services.Downloads;

internal sealed class DownloadQueueStore(string path) : IDisposable
{
    internal const int Capacity = 1000;
    private FileStream? _lease;

    internal sealed record QueueState(bool IsPaused, IReadOnlyList<DownloadJob> Jobs);

    internal QueueState Load()
    {
        var directory = Path.GetDirectoryName(path)!;
        if (StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(directory) ||
            StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(path + ".lock"))
            throw new IOException("The download queue path is unsafe.");

        Directory.CreateDirectory(directory);
        _lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(path))
            throw new IOException("The download queue path is unsafe.");

        if (!File.Exists(path))
            return new(false, []);

        using var stream = File.OpenRead(path);
        if (stream.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("The download queue exceeds its size limit.");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var state = root.ValueKind == JsonValueKind.Array
            ? new QueueState(false, root.Deserialize<DownloadJob[]>()!)
            : root.Deserialize<QueueState>();
        if (state?.Jobs is not { } jobs || jobs.Count > Capacity || jobs.Any(job => !IsValid(job)) ||
            jobs.Select(job => job.Id).Distinct().Count() != jobs.Count)
            throw new InvalidDataException("The download queue could not be validated.");

        return state;
    }

    internal void Save(IReadOnlyList<DownloadJob> jobs, bool isPaused = false)
    {
        if (_lease == null)
            throw new InvalidOperationException("The download queue is not open.");

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new QueueState(isPaused, jobs));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static bool IsValid(DownloadJob? job) =>
        job != null && job.Id != Guid.Empty && IsIdentifier(job.ItemId) &&
        (job.Revision is "" || job.Revision is { Length: 64 } && job.Revision.All(Uri.IsHexDigit)) &&
        (job.PayloadRevision == null || job.Store == DownloadStore.Gog &&
            job.PayloadRevision.Length == 64 && job.PayloadRevision.All(Uri.IsHexDigit)) &&
        Enum.IsDefined(job.Store) && Enum.IsDefined(job.Kind) && Enum.IsDefined(job.State) && Enum.IsDefined(job.Reason) &&
        (job.Platform is null or "windows" or "linux") &&
        (job.StoreGameId == null || IsIdentifier(job.StoreGameId)) &&
        (job.Kind != DownloadKind.OfflineInstaller || job.Store == DownloadStore.Gog);

    private static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}
