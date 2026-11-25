using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog;

internal sealed class GogPackageHashDb
{
    private const int HeaderSize = 12;
    private const int PathSize = 1024;
    private const int RecordSize = PathSize + 32;
    private const int MaxRecords = 100_000;
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    private readonly IReadOnlyList<Entry> _entries;

    private GogPackageHashDb(IReadOnlyList<Entry> entries) => _entries = entries;

    internal int Count => _entries.Count;

    // RAR-backed installers can spell package-relative paths with a single leading slash.
    // Accept that spelling only when the same path exists in the archive's game/ inventory.
    internal static GogPackageHashDb Read(
        Stream data, string name, IReadOnlyDictionary<string, long>? archiveSizes = null,
        bool allowArchiveRootMarker = false)
    {
        using var zip = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count != 1 ||
            (zip.Entries[0].FullName != name && zip.Entries[0].FullName != name + ".temporary"))
        {
            throw new InvalidDataException("Unexpected GOG HashDB ZIP entry.");
        }

        var member = zip.Entries[0];
        using var stream = member.Open();
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != HeaderSize ||
            BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != 1 ||
            count is <= 0 or > MaxRecords || member.Length != HeaderSize + (long)count * RecordSize)
        {
            throw new InvalidDataException("Unsupported GOG HashDB header or record count.");
        }

        var entries = new List<Entry>(count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Span<byte> record = stackalloc byte[RecordSize];
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(record);
            var terminator = record[..PathSize].IndexOf((byte)0);
            var rawPath = Utf8.GetString(record[..(terminator < 0 ? PathSize : terminator)]);
            var archiveRelative = allowArchiveRootMarker && archiveSizes != null && rawPath.StartsWith('/');
            var path = NormalizeRelativePath(archiveRelative ? rawPath[1..] : rawPath);
            var checksum = Encoding.ASCII.GetString(record[PathSize..]);
            if (path == null || !paths.Add(path) || !checksum.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid or duplicate path or digest in GOG HashDB.");

            long? size = null;
            if (archiveSizes != null)
            {
                if (!archiveSizes.TryGetValue(path, out var expectedSize))
                    throw new InvalidDataException("GOG HashDB paths do not match the package archive.");
                size = expectedSize;
            }

            entries.Add(new Entry(path, Convert.FromHexString(checksum), size));
        }

        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Unexpected data after GOG HashDB records.");

        if (archiveSizes != null && archiveSizes.TryGetValue(name, out var hashDbSize))
        {
            if (!paths.Add(name))
                throw new InvalidDataException("The GOG HashDB unexpectedly describes itself.");
            data.Position = 0;
            entries.Add(new Entry(name, MD5.HashData(data), hashDbSize, VerifyAlways: true));
        }

        return new GogPackageHashDb(entries);
    }

    internal static string? NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => char.IsControl(c) || c is ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            {
                return null;
            }
        }

        return normalized;
    }

    internal Dictionary<string, FileStamp> CaptureExistingFiles(string installPath, CancellationToken ct)
    {
        installPath = Path.GetFullPath(installPath);
        var existing = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        var directories = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            var file = GetRegularFile(installPath, entry.Path, directories);
            if (file != null)
                existing.Add(entry.Path, new FileStamp(file.Length, file.LastWriteTimeUtc));
        }

        return existing;
    }

    internal async Task<string?> ValidateAsync(
        string installPath, IReadOnlyDictionary<string, FileStamp> before,
        bool verifyAllHashes, CancellationToken ct)
    {
        installPath = Path.GetFullPath(installPath);
        var directories = new Dictionary<string, bool>(StringComparer.Ordinal);
        var unchanged = new List<(Entry Entry, string FullPath)>();
        foreach (var entry in _entries)
        {
            ct.ThrowIfCancellationRequested();
            var file = GetRegularFile(installPath, entry.Path, directories);
            if (file == null)
                return $"Required package file is missing or is not a regular file: '{entry.Path}'.";
            if (entry.Size is { } size && file.Length != size)
                return $"Required package file has the wrong size: '{entry.Path}'.";

            if (verifyAllHashes || entry.VerifyAlways || before.TryGetValue(entry.Path, out var previous) &&
                previous == new FileStamp(file.Length, file.LastWriteTimeUtc))
            {
                unchanged.Add((entry, file.FullName));
            }
        }

        // Do not read file contents until the inexpensive completeness checks have passed.
        // Existing files with unchanged metadata may belong to an earlier installation.
        foreach (var (entry, fullPath) in unchanged)
        {
            ct.ThrowIfCancellationRequested();
            if (GetRegularFile(installPath, entry.Path, new Dictionary<string, bool>(StringComparer.Ordinal)) == null)
                return $"Could not safely read required package file: '{entry.Path}'.";

            try
            {
                await using var stream = File.OpenRead(fullPath);
                var actual = await MD5.HashDataAsync(stream, ct).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(entry.Md5))
                    return $"Required package file has the wrong MD5 checksum: '{entry.Path}'.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not read required package file: '{entry.Path}'.";
            }
        }

        return null;
    }

    private static FileInfo? GetRegularFile(string root, string relativePath, Dictionary<string, bool> directories)
    {
        try
        {
            var fullPath = Path.Combine(root, relativePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            if (!IsRealDirectory(directory, directories))
                return null;

            var file = new FileInfo(fullPath);
            return file.LinkTarget == null && file.Exists
                ? file
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsRealDirectory(string path, Dictionary<string, bool> directories)
    {
        if (directories.TryGetValue(path, out var cached))
            return cached;

        var parent = Path.GetDirectoryName(path);
        var directory = new DirectoryInfo(path);
        var safe = (parent == null || IsRealDirectory(parent, directories)) &&
            directory.LinkTarget == null && directory.Exists;
        directories[path] = safe;
        return safe;
    }

    internal readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);
    private sealed record Entry(string Path, byte[] Md5, long? Size, bool VerifyAlways = false);
}
