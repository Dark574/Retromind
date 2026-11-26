using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal sealed record GogPackageFileMetadata(GogPackageHashDb? HashDb = null, GogInnoFileList? InnoFiles = null);

internal static class GogPackageHashDbReader
{
    private const int MaxHashDbBytes = 16 * 1024 * 1024;
    private const int MaxListingBytes = 16 * 1024 * 1024;
    private const int MaxCombinedInnoListingsBytes = 64 * 1024 * 1024;
    private static readonly string[] InnoOptions =
        ["--silent", "--no-extract-unknown", "--collisions=error", "--exclude-temp"];

    internal static async Task<GogPackageFileMetadata?> TryReadAsync(
        GogDownloadedInstallerPackage package, string installer, string gameId,
        Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameId) || !gameId.All(char.IsAsciiDigit))
            return null;

        var name = $"goggame-{gameId}.hashdb";
        var directory = Path.GetDirectoryName(installer)!;
        var stem = Path.GetFileNameWithoutExtension(installer);
        var parts = package.DownloadedFiles.Distinct(StringComparer.Ordinal)
            .Where(path => FileSystemPathIdentity.Equals(Path.GetDirectoryName(path), directory) &&
                IsCompanionPart(Path.GetFileName(path), stem))
            .ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (parts.Any(IsRar))
            {
                var manifest = await TryReadRarAsync(parts, name, deadline.Token).ConfigureAwait(false);
                return manifest == null ? null : new(HashDb: manifest);
            }

            return await TryReadInnoAsync(installer, name, stem, parts, log, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log("[Package files] Reading the GOG package manifest timed out.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   Win32Exception or DecoderFallbackException or NotSupportedException)
        {
            log($"[Package files] Could not read the GOG package manifest: {ex.Message}");
            return null;
        }
    }

    private static async Task<GogPackageHashDb?> TryReadRarAsync(string[] parts, string name, CancellationToken ct)
    {
        var sevenZip = AppImageToolResolver.ResolveBundledExecutable("7zz") ??
                       EnvironmentPathHelper.TryFindExecutableInCurrentPath("7zz") ??
                       EnvironmentPathHelper.TryFindExecutableInCurrentPath("7z");
        if (sevenZip == null)
            return null;

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        string? hashDbArchive = null;
        foreach (var part in parts)
        {
            if (!IsRar(part))
                return null;

            using var listing = await ReadToolOutputAsync(sevenZip,
                ["l", "-slt", "-ba", "-sccUTF-8", "--", part], MaxListingBytes, ct).ConfigureAwait(false);
            using var reader = new StreamReader(listing, new UTF8Encoding(false, true));
            if (ReadArchiveInventory(reader, name, sizes))
            {
                if (hashDbArchive != null)
                    throw new InvalidDataException("The GOG package contains more than one matching HashDB.");
                hashDbArchive = part;
            }
        }

        if (hashDbArchive == null)
            return null;

        using var hashDb = await ReadToolOutputAsync(sevenZip,
            ["x", "-so", $"-ir!{name}", "--", hashDbArchive], MaxHashDbBytes, ct).ConfigureAwait(false);
        if (hashDb.Length != sizes[name])
            throw new InvalidDataException("Extracted GOG HashDB size does not match the package archive.");

        return GogPackageHashDb.Read(hashDb, name, sizes, allowArchiveRootMarker: true);
    }

    private static async Task<GogPackageFileMetadata?> TryReadInnoAsync(
        string installer, string name, string stem, string[] parts, Action<string> log, CancellationToken ct)
    {
        var innoextract = AppImageToolResolver.ResolveBundledExecutable("innoextract") ??
                          EnvironmentPathHelper.TryFindExecutableInCurrentPath("innoextract");
        if (innoextract == null)
        {
            log("[Package files] innoextract is not available in PATH.");
            return null;
        }

        // Inno opens companion files by name. An undeclared neighbor must not influence the result.
        var sourceDirectory = Path.GetDirectoryName(installer)!;
        if (Directory.EnumerateFiles(sourceDirectory).Any(path =>
                IsCompanionPart(Path.GetFileName(path), stem) && !parts.Contains(path, StringComparer.Ordinal)))
        {
            return null;
        }

        async Task<GogPackageHashDb?> ReadHashDbAsync(string[] options)
        {
            try
            {
                return await TryReadInnoHashDbAsync(innoextract, installer, name, options, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or DecoderFallbackException)
            {
                log($"[Package files] Inno HashDB could not be read: {ex.Message}");
                return null;
            }
        }

        using var languageOutput = await ReadToolOutputAsync(innoextract,
            [.. InnoOptions, "--list-languages", "--", installer], MaxListingBytes, ct).ConfigureAwait(false);
        using var languageReader = new StreamReader(languageOutput, new UTF8Encoding(false, true));
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (languageReader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var language = line.Split(' ', 2)[0];
            if (language.Length == 0 || !language.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new InvalidDataException("Unexpected language in the Inno language list.");

            languages.Add(language);
            if (languages.Count > 64)
            {
                throw new InvalidDataException("The Inno language list exceeds the supported size.");
            }
        }

        var variants = languages.Count == 0
            ? [InnoOptions]
            : languages.Select(language => (string[])[.. InnoOptions, "--language", language]).ToArray();
        var common = await ReadHashDbAsync(variants[0]).ConfigureAwait(false);
        for (var i = 1; common != null && i < variants.Length; i++)
        {
            var current = await ReadHashDbAsync(variants[i]).ConfigureAwait(false);
            if (current == null || !common.HasSameEntries(current))
                common = null;
        }
        if (common != null)
            return new(HashDb: common);

        var fileList = new GogInnoFileList();
        var remainingBytes = MaxCombinedInnoListingsBytes;
        foreach (var options in variants)
        {
            using var listing = await ReadToolOutputAsync(innoextract,
                [.. options, "--dump", "--list-sizes", "--list-checksums", "--", installer],
                Math.Min(MaxListingBytes, remainingBytes), ct).ConfigureAwait(false);
            remainingBytes -= (int)listing.Length;
            using var reader = new StreamReader(listing, new UTF8Encoding(false, true));
            fileList.AddLanguage(reader, name);
        }
        return fileList.Count == 0 ? null : new(InnoFiles: fileList);
    }

    private static async Task<GogPackageHashDb?> TryReadInnoHashDbAsync(
        string innoextract, string installer, string name, string[] options, CancellationToken ct)
    {
        using var listing = await ReadToolOutputAsync(innoextract,
            [.. options, "--list-sizes", "--", installer], MaxListingBytes, ct).ConfigureAwait(false);
        using var reader = new StreamReader(listing, new UTF8Encoding(false, true));
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            // Innoextract lists explicit directories without a size, even with --list-sizes.
            if (line.EndsWith('/') && GogPackageHashDb.NormalizeRelativePath(line[..^1]) != null)
                continue;

            var separator = line.IndexOf(' ');
            if (separator <= 0 ||
                !long.TryParse(line.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException("Unexpected entry in the Inno Setup archive inventory.");
            }

            var path = GogPackageHashDb.NormalizeRelativePath(line[(separator + 1)..]);
            if (path != null && path != name && path.Split('/').Contains(name, StringComparer.OrdinalIgnoreCase))
                return null;
            if (path != null && !sizes.TryAdd(path, size))
                throw new InvalidDataException("Duplicate path in the Inno Setup archive inventory.");
        }

        if (!sizes.TryGetValue(name, out var expectedSize) || expectedSize is <= 0 or > MaxHashDbBytes)
        {
            return null;
        }

        // innoextract can write a single selected entry, but cannot stream it to stdout.
        var output = Directory.CreateTempSubdirectory("retromind-hashdb-").FullName;
        try
        {
            using var extractLog = await ReadToolOutputAsync(innoextract,
                [.. options, "--extract", "--include", name, "--output-dir", output, "--", installer],
                MaxListingBytes, ct).ConfigureAwait(false);
            var file = new FileInfo(Path.Combine(output, name));
            if (file.LinkTarget != null || !file.Exists || file.Length != expectedSize)
                throw new InvalidDataException("Extracted GOG HashDB does not match the Inno Setup archive inventory.");

            using var hashDb = file.OpenRead();
            return GogPackageHashDb.Read(hashDb, name, sizes);
        }
        finally
        {
            GogInstallDirectorySafety.DeleteDirectoryTreeWithoutFollowingLinks(output);
        }
    }

    private static bool IsCompanionPart(string fileName, string stem)
    {
        if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = fileName.AsSpan(0, fileName.Length - 4);
        if (name.Equals(stem, StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Length <= stem.Length + 1 || name[stem.Length] != '-' ||
            !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var digit in name[(stem.Length + 1)..])
        {
            if (!char.IsAsciiDigit(digit))
                return false;
        }

        return true;
    }

    private static bool IsRar(string path)
    {
        using var archive = File.OpenRead(path);
        Span<byte> header = stackalloc byte[7];
        return archive.ReadAtLeast(header, header.Length, false) == header.Length &&
               header[..6].SequenceEqual("Rar!\x1a\x07"u8) && header[6] is 0 or 1;
    }

    private static bool ReadArchiveInventory(
        StreamReader reader, string name, Dictionary<string, long> sizes)
    {
        string? path = null;
        long length = -1;
        var file = false;
        var hashDb = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                AddEntry();
                path = null;
                length = -1;
                file = false;
            }
            else if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (path != null)
                    throw new InvalidDataException("Ambiguous path in the GOG archive inventory.");
                path = line[7..].Replace('\\', '/');
            }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal))
            {
                if (!long.TryParse(line.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out length))
                    throw new InvalidDataException("Invalid file size in the GOG archive inventory.");
            }
            else if (line == "Folder = -")
            {
                file = true;
            }
        }

        AddEntry();
        return hashDb;

        void AddEntry()
        {
            if (!file || path == null)
                return;

            if (Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (hashDb || path != "game/" + name || length is <= 0 or > MaxHashDbBytes)
                    throw new InvalidDataException("Unexpected or duplicate HashDB in the GOG archive.");
                hashDb = true;
            }

            if (path.StartsWith("game/", StringComparison.Ordinal))
            {
                var relative = GogPackageHashDb.NormalizeRelativePath(path[5..]);
                if (relative == null || length < 0 || !sizes.TryAdd(relative, length))
                    throw new InvalidDataException("Invalid or duplicate game file in the GOG archive inventory.");
            }
        }
    }

    private static async Task<MemoryStream> ReadToolOutputAsync(
        string executable, IReadOnlyList<string> arguments, int limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        HostProcessEnvironmentSanitizer.Sanitize(start);
        AppImageToolResolver.ConfigureBundledToolEnvironment(start, executable);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the archive reader.");
        process.StandardInput.Close();
        using var output = new MemoryStream();
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        try
        {
            var buffer = new byte[16 * 1024];
            var source = process.StandardOutput.BaseStream;
            int size;
            while ((size = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + size > limit)
                    throw new InvalidDataException("GOG archive metadata exceeds the supported size.");
                output.Write(buffer, 0, size);
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"Archive reader exited with code {process.ExitCode}.");

            return new MemoryStream(output.GetBuffer(), 0, (int)output.Length, writable: false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Process exited before the kill. */ }
            }
        }
    }
}
