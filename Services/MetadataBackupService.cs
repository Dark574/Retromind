using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services;

public enum MetadataBackupReason
{
    Unknown,
    Manual,
    BeforeBulkEdit,
    BeforeBulkScrape,
    OnStartup,
    BeforeRestore
}

public sealed record MetadataBackupInfo(
    string FilePath,
    string FileName,
    DateTimeOffset CreatedUtc,
    MetadataBackupReason Reason,
    string RetromindVersion,
    long ArchiveSizeBytes,
    bool IsValid,
    string? ValidationError = null);

public sealed record MetadataBackupContent(string LibraryJson, string SettingsJson);

/// <summary>
/// Creates and validates small, versioned backups of Retromind's library and settings JSON.
/// Media, games, themes and portable HOME data are intentionally outside this backup scope.
/// </summary>
public sealed class MetadataBackupService
{
    private const int BackupFormatVersion = 1;
    private const int AutomaticBackupRetention = 10;
    private const long MaximumJsonEntryBytes = 512L * 1024 * 1024;
    private const long MaximumManifestBytes = 64L * 1024;
    private const string ArchivePrefix = "RetromindMetadata-";
    private const string LibraryEntryName = "retromind_tree.json";
    private const string SettingsEntryName = "app_settings.json";
    private const string ManifestEntryName = "backup-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public MetadataBackupService()
        : this(AppPaths.DataRoot)
    {
    }

    internal MetadataBackupService(string dataRoot, Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("A data root is required.", nameof(dataRoot));

        _dataRoot = Path.GetFullPath(dataRoot);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string BackupDirectory => Path.Combine(_dataRoot, "Backups");

    public async Task<MetadataBackupInfo> CreateBackupAsync(
        MetadataBackupContent content,
        MetadataBackupReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (reason is not (MetadataBackupReason.Manual or
            MetadataBackupReason.BeforeBulkEdit or
            MetadataBackupReason.BeforeBulkScrape or
            MetadataBackupReason.OnStartup or
            MetadataBackupReason.BeforeRestore))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ValidateJson(content.LibraryJson, LibraryEntryName);
        ValidateJson(content.SettingsJson, SettingsEntryName);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(BackupDirectory);

            var createdUtc = _utcNow().ToUniversalTime();
            var finalPath = BuildUniqueArchivePath(createdUtc);
            temporaryPath = Path.Combine(
                BackupDirectory,
                $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");

            var libraryBytes = Encoding.UTF8.GetBytes(content.LibraryJson);
            var settingsBytes = Encoding.UTF8.GetBytes(content.SettingsJson);
            var manifest = new MetadataBackupManifest
            {
                FormatVersion = BackupFormatVersion,
                CreatedUtc = createdUtc,
                Reason = reason,
                RetromindVersion = ResolveRetromindVersion(),
                LibrarySha256 = ComputeSha256(libraryBytes),
                SettingsSha256 = ComputeSha256(settingsBytes)
            };

            await WriteArchiveAsync(
                    temporaryPath,
                    libraryBytes,
                    settingsBytes,
                    JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            // Never publish a partially written or unreadable archive.
            _ = await ReadBackupCoreAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: false);
            temporaryPath = null;

            await PruneAutomaticBackupsCoreAsync(cancellationToken).ConfigureAwait(false);
            return await ReadBackupInfoCoreAsync(finalPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryPath != null)
                TryDeleteFile(temporaryPath);

            _ioGate.Release();
        }
    }

    public async Task<IReadOnlyList<MetadataBackupInfo>> GetBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(BackupDirectory))
                return Array.Empty<MetadataBackupInfo>();

            var backups = new List<MetadataBackupInfo>();
            foreach (var path in Directory.EnumerateFiles(
                         BackupDirectory,
                         ArchivePrefix + "*.zip",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    backups.Add(await ReadBackupInfoCoreAsync(path, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var file = new FileInfo(path);
                    backups.Add(new MetadataBackupInfo(
                        path,
                        file.Name,
                        file.LastWriteTimeUtc,
                        MetadataBackupReason.Unknown,
                        string.Empty,
                        file.Exists ? file.Length : 0,
                        IsValid: false,
                        ex.Message));
                }
            }

            return backups
                .OrderByDescending(backup => backup.CreatedUtc)
                .ThenByDescending(backup => backup.FileName, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<MetadataBackupContent> ReadBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var safePath = ResolveManagedBackupPath(backupPath);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadBackupCoreAsync(safePath, cancellationToken).ConfigureAwait(false)).Content;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task DeleteBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var safePath = ResolveManagedBackupPath(backupPath);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(safePath);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task PruneAutomaticBackupsCoreAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(BackupDirectory))
            return;

        var automaticBackups = new List<MetadataBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(
                     BackupDirectory,
                     ArchivePrefix + "*.zip",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var backup = await ReadBackupInfoCoreAsync(path, cancellationToken).ConfigureAwait(false);
                if (backup.Reason is MetadataBackupReason.BeforeBulkEdit or
                    MetadataBackupReason.BeforeBulkScrape or
                    MetadataBackupReason.OnStartup)
                    automaticBackups.Add(backup);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Invalid archives stay visible in the manager and are never deleted automatically.
            }
        }

        foreach (var obsolete in automaticBackups
                     .OrderByDescending(backup => backup.CreatedUtc)
                     .ThenByDescending(backup => backup.FileName, StringComparer.Ordinal)
                     .Skip(AutomaticBackupRetention))
        {
            TryDeleteFile(obsolete.FilePath);
        }
    }

    private async Task<MetadataBackupInfo> ReadBackupInfoCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestCoreAsync(path, cancellationToken).ConfigureAwait(false);
        var file = new FileInfo(path);
        return new MetadataBackupInfo(
            file.FullName,
            file.Name,
            manifest.CreatedUtc,
            manifest.Reason,
            manifest.RetromindVersion,
            file.Length,
            IsValid: true);
    }

    private static async Task<MetadataBackupManifest> ReadManifestCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        return await ReadAndValidateManifestAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ValidatedBackup> ReadBackupCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var manifest = await ReadAndValidateManifestAsync(archive, cancellationToken).ConfigureAwait(false);

        var libraryBytes = await ReadRequiredEntryAsync(
                archive,
                LibraryEntryName,
                MaximumJsonEntryBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var settingsBytes = await ReadRequiredEntryAsync(
                archive,
                SettingsEntryName,
                MaximumJsonEntryBytes,
                cancellationToken)
            .ConfigureAwait(false);

        VerifyHash(libraryBytes, manifest.LibrarySha256, LibraryEntryName);
        VerifyHash(settingsBytes, manifest.SettingsSha256, SettingsEntryName);

        var libraryJson = Encoding.UTF8.GetString(libraryBytes);
        var settingsJson = Encoding.UTF8.GetString(settingsBytes);
        ValidateJson(libraryJson, LibraryEntryName);
        ValidateJson(settingsJson, SettingsEntryName);

        return new ValidatedBackup(
            manifest,
            new MetadataBackupContent(libraryJson, settingsJson));
    }

    private static async Task<MetadataBackupManifest> ReadAndValidateManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var manifestBytes = await ReadRequiredEntryAsync(
                archive,
                ManifestEntryName,
                MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<MetadataBackupManifest>(manifestBytes, ManifestJsonOptions)
                       ?? throw new InvalidDataException("The backup manifest is empty.");

        if (manifest.FormatVersion != BackupFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported metadata backup format {manifest.FormatVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (manifest.CreatedUtc == default ||
            manifest.Reason is not (MetadataBackupReason.Manual or
                MetadataBackupReason.BeforeBulkEdit or
                MetadataBackupReason.BeforeBulkScrape or
                MetadataBackupReason.OnStartup or
                MetadataBackupReason.BeforeRestore) ||
            string.IsNullOrWhiteSpace(manifest.LibrarySha256) ||
            string.IsNullOrWhiteSpace(manifest.SettingsSha256))
        {
            throw new InvalidDataException("The backup manifest is incomplete.");
        }

        ValidateEntryMetadata(archive, LibraryEntryName, MaximumJsonEntryBytes);
        ValidateEntryMetadata(archive, SettingsEntryName, MaximumJsonEntryBytes);
        return manifest;
    }

    private static void ValidateEntryMetadata(ZipArchive archive, string entryName, long maximumBytes)
    {
        var matchingEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
            .ToArray();
        if (matchingEntries.Length != 1)
            throw new InvalidDataException($"The backup must contain exactly one '{entryName}' entry.");

        if (matchingEntries[0].Length < 0 || matchingEntries[0].Length > maximumBytes)
            throw new InvalidDataException($"The backup entry '{entryName}' is too large.");
    }

    private static async Task<byte[]> ReadRequiredEntryAsync(
        ZipArchive archive,
        string entryName,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var matchingEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
            .ToArray();
        if (matchingEntries.Length != 1)
            throw new InvalidDataException($"The backup must contain exactly one '{entryName}' entry.");

        var entry = matchingEntries[0];
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException($"The backup entry '{entryName}' is too large.");

        await using var entryStream = entry.Open();
        using var memory = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await entryStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        if (memory.Length > maximumBytes)
            throw new InvalidDataException($"The backup entry '{entryName}' is too large.");

        return memory.ToArray();
    }

    private static async Task WriteArchiveAsync(
        string path,
        byte[] libraryBytes,
        byte[] settingsBytes,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        await WriteEntryAsync(archive, LibraryEntryName, libraryBytes, cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, SettingsEntryName, settingsBytes, cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, ManifestEntryName, manifestBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await entryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private string BuildUniqueArchivePath(DateTimeOffset createdUtc)
    {
        var stem = ArchivePrefix + createdUtc.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(BackupDirectory, stem + ".zip");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                BackupDirectory,
                stem + "-" + suffix.ToString(CultureInfo.InvariantCulture) + ".zip");
            suffix++;
        }

        return candidate;
    }

    private string ResolveManagedBackupPath(string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            throw new ArgumentException("A backup path is required.", nameof(backupPath));

        var fullPath = Path.GetFullPath(backupPath);
        var backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BackupDirectory));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullPath) ?? string.Empty);
        if (!string.Equals(parent, backupRoot, StringComparison.Ordinal) ||
            !Path.GetFileName(fullPath).StartsWith(ArchivePrefix, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected file is not a managed Retromind metadata backup.");
        }

        return fullPath;
    }

    private static void ValidateJson(string json, string entryName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"The backup entry '{entryName}' is empty.");

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The backup entry '{entryName}' contains invalid JSON.", ex);
        }
    }

    private static void VerifyHash(byte[] bytes, string expectedHash, string entryName)
    {
        if (!string.Equals(ComputeSha256(bytes), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The backup entry '{entryName}' failed its checksum validation.");
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ResolveRetromindVersion()
        => typeof(MetadataBackupService).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? typeof(MetadataBackupService).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort for failed temporary files and automatic retention.
        }
    }

    private sealed class MetadataBackupManifest
    {
        public int FormatVersion { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }
        public MetadataBackupReason Reason { get; init; }
        public string RetromindVersion { get; init; } = string.Empty;
        public string LibrarySha256 { get; init; } = string.Empty;
        public string SettingsSha256 { get; init; } = string.Empty;
    }

    private sealed record ValidatedBackup(
        MetadataBackupManifest Manifest,
        MetadataBackupContent Content);
}
