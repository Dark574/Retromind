using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog;

internal sealed class GogInnoFileList
{
    private readonly Dictionary<string, List<FileEntry>> _files = new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _files.Count;

    internal void AddLanguage(StreamReader reader, string hashDbName)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            // In silent mode an explicit directory has a trailing slash and no size.
            if (line.EndsWith('/'))
                continue;

            var fields = line.Split(' ', 4);
            if (fields.Length != 4 ||
                !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException("Unexpected entry in the Inno file list.");
            }

            // --dump preserves the destination constant for ordinary Inno files. GOG Galaxy
            // files reconstructed by innoextract already have package-relative paths.
            var rawPath = fields[3].Replace('\\', '/');
            var inApp = rawPath.StartsWith("{app}/", StringComparison.OrdinalIgnoreCase);
            var relative = inApp ? rawPath[6..] : rawPath;
            if (!inApp && rawPath.StartsWith('{'))
                continue;
            var path = GogPackageHashDb.NormalizeRelativePath(relative);
            if (path == null || path.Contains('{') || path.Contains('}'))
                throw new InvalidDataException("Unsafe destination in the Inno file list.");

            if (!paths.Add(path))
                throw new InvalidDataException("Duplicate destination in the Inno file list.");
            if (IsSetupSupportFile(path, hashDbName))
                continue;

            var digestLength = fields[1] switch { "MD5" => 32, "SHA-1" => 40, "SHA-256" => 64, _ => 0 };
            if (digestLength == 0 || fields[2].Length != digestLength || !fields[2].All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid or unsupported checksum in the Inno file list.");

            var entry = new FileEntry(path, size, fields[1], fields[2].ToUpperInvariant());
            if (!_files.TryGetValue(path, out var variants))
                _files.Add(path, [entry]);
            else if (variants[0].Path != path)
                throw new InvalidDataException("Ambiguous destination casing in the Inno file list.");
            else if (!variants.Contains(entry))
                variants.Add(entry);
        }
    }

    internal async Task<Verification> ValidateAsync(
        string installPath, string windowsDestination, string setupLog, CancellationToken ct)
    {
        var root = Path.GetFullPath(installPath);
        var required = ReadInstalledDestinations(windowsDestination, setupLog, ct);
        var directories = new Dictionary<string, bool>(StringComparer.Ordinal);
        var verified = 0;
        foreach (var (path, variants) in _files)
        {
            ct.ThrowIfCancellationRequested();
            var file = GogPackageHashDb.GetRegularFile(root, path, directories);
            if (file == null)
            {
                if (required.Contains(path) && IsKnownMissingFile(root, path, directories))
                    return new(verified, Count, $"Required package file is missing: '{path}'.");
                continue;
            }

            try
            {
                if (await MatchesAsync(file, variants, ct).ConfigureAwait(false))
                    verified++;
                else if (required.Contains(path))
                    return new(verified, Count, $"Required package file has the wrong size or checksum: '{path}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read failure does not establish that the file is corrupt.
            }
        }

        return new(verified, Count);
    }

    private static bool IsSetupSupportFile(string path, string hashDbName)
    {
        // GOG can stage setup helpers in these directories under {app} and remove them after use.
        if (path.Equals(hashDbName, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("__redist/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("__support/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Inno's generated uninstaller files are not part of the game payload.
        return path.Length == 12 && path.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
               char.IsAsciiDigit(path[5]) && char.IsAsciiDigit(path[6]) && char.IsAsciiDigit(path[7]) &&
               (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".msg", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> ReadInstalledDestinations(string windowsDestination, string setupLog, CancellationToken ct)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var prefix = windowsDestination.TrimEnd('\\') + '\\';
        try
        {
            var inFileEntry = false;
            string? path = null;
            foreach (var line in File.ReadLines(setupLog))
            {
                ct.ThrowIfCancellationRequested();
                var separator = line.IndexOf("   ", StringComparison.Ordinal);
                if (separator < 0)
                    continue;
                var message = line[(separator + 3)..];
                if (message.StartsWith("-- ", StringComparison.Ordinal))
                {
                    inFileEntry = message == "-- File entry --";
                    path = null;
                }
                else if (inFileEntry && message.StartsWith("Dest filename: ", StringComparison.Ordinal))
                {
                    var destination = message[15..];
                    path = destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? GogPackageHashDb.NormalizeRelativePath(destination[prefix.Length..])
                        : null;
                }
                else if (path != null && message == "Successfully installed the file.")
                {
                    installed.Add(path);
                    path = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            installed.Clear();
        }

        return installed;
    }

    private static bool IsKnownMissingFile(string root, string relativePath, Dictionary<string, bool> directories)
    {
        // Check each parent before looking at attributes of its child. An inaccessible path
        // or a symbolic link cannot prove that a required file is missing.
        var parent = root;
        foreach (var segment in relativePath.Split('/'))
        {
            if (!GogPackageHashDb.IsRealDirectory(parent, directories))
                return false;
            parent = Path.Combine(parent, segment);
            try { _ = File.GetAttributes(parent); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        return false;
    }

    private static async Task<bool> MatchesAsync(FileInfo file, List<FileEntry> variants, CancellationToken ct)
    {
        var length = file.Length;
        var sizesThatMatch = variants.Where(variant => variant.Size == length);
        foreach (var group in sizesThatMatch.GroupBy(variant => variant.Algorithm))
        {
            await using var stream = file.OpenRead();
            var checksum = group.Key switch
            {
                "MD5" => await MD5.HashDataAsync(stream, ct).ConfigureAwait(false),
                "SHA-1" => await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false),
                _ => await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)
            };
            var digest = Convert.ToHexString(checksum);
            if (group.Any(variant => variant.Digest == digest))
                return true;
        }

        return false;
    }

    private sealed record FileEntry(string Path, long Size, string Algorithm, string Digest);
    internal sealed record Verification(int Verified, int Total, string? Failure = null)
    {
        internal bool Complete => Verified > 0 && Verified == Total && Failure == null;
    }
}
