using System;
using System.IO;

namespace Retromind.Helpers;

/// <summary>
/// Prevents multiple Retromind processes from writing to the same portable data root.
/// The lock file intentionally remains on disk; only the exclusive open handle is the lock.
/// </summary>
internal sealed class ApplicationInstanceLock : IDisposable
{
    private const string LockFileName = ".retromind.instance.lock";

    private FileStream? _stream;

    private ApplicationInstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ApplicationInstanceLock Acquire(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("A data root is required.", nameof(dataRoot));

        var fullDataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(fullDataRoot);
        var lockPath = Path.Combine(fullDataRoot, LockFileName);
        var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        return new ApplicationInstanceLock(stream);
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
