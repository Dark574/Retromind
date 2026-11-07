using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Resolves the media path passed to a launcher and creates optional multi-disc playlists.
/// </summary>
public sealed class LaunchPlaylistService
{
    private readonly string _libraryRootPath;
    private readonly IErrorService _errorService;
    private readonly Action<Stream, IReadOnlyList<string>> _writePlaylist;

    public LaunchPlaylistService(string libraryRootPath, IErrorService errorService)
        : this(libraryRootPath, errorService, WritePlaylist)
    {
    }

    internal LaunchPlaylistService(string libraryRootPath, IErrorService errorService,
        Action<Stream, IReadOnlyList<string>> writePlaylist)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _writePlaylist = writePlaylist ?? throw new ArgumentNullException(nameof(writePlaylist));
    }

    public string? ResolveLaunchFilePath(
        MediaItem item,
        List<string>? nodePath,
        bool usePlaylistForMultiDisc)
    {
        if (!usePlaylistForMultiDisc || item.Files is not { Count: > 1 } || nodePath is not { Count: > 0 })
            return item.GetPrimaryLaunchPath();

        // Missing references can arrive from malformed persisted data. In that case the
        // primary-disc selection is ambiguous, so do not invent a different launch target.
        var primary = item.Files.Exists(file => file == null) ? null : item.GetPrimaryLaunchPath();
        // A malformed primary path cannot provide a safe fallback either.
        if (!IsValidEntry(primary))
            primary = null;

        var playlistPath = CreateOrUpdatePlaylist(item, nodePath, primary);
        return string.IsNullOrWhiteSpace(playlistPath) ? primary : playlistPath;
    }

    private string? CreateOrUpdatePlaylist(MediaItem item, List<string> nodePath, string? primary)
    {
        string? fullPath = null;
        string? temporaryPath = null;
        var operation = "ResolveDiscs";
        var context = new Dictionary<string, string>
        {
            ["ItemId"] = item.Id,
            ["DiscCount"] = item.Files.Count.ToString(CultureInfo.InvariantCulture),
            ["FallbackToPrimary"] = (primary != null).ToString()
        };
        Exception? failure = null;
        try
        {
            var ordered = new List<MediaFileRef>(item.Files);
            if (ordered.Exists(file => file == null))
                throw new InvalidDataException("A configured disc reference is missing.");

            ordered.Sort(static (left, right) =>
            {
                var leftIndex = left.Index ?? int.MaxValue;
                var rightIndex = right.Index ?? int.MaxValue;
                var comparison = leftIndex.CompareTo(rightIndex);
                if (comparison != 0)
                    return comparison;

                comparison = string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
                return comparison != 0
                    ? comparison
                    : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            });

            var lines = new List<string>(ordered.Count);
            foreach (var file in ordered)
            {
                var resolved = file.Kind switch
                {
                    MediaFileKind.Absolute => file.Path,
                    MediaFileKind.LibraryRelative => AppPaths.ResolveDataPathInsideRootOrEmpty(file.Path),
                    _ => null
                };
                if (!IsValidEntry(file.Path) || !IsValidEntry(resolved))
                {
                    context["DiscPath"] = file.Path;
                    context["DiscKind"] = file.Kind.ToString();
                    throw new InvalidDataException("A configured disc has an empty, invalid, or unresolvable playlist path.");
                }

                lines.Add(resolved!);
            }

            operation = "CreateDirectory";
            var nodeFolder = PathHelper.ResolveNodeFolder(nodePath, _libraryRootPath);
            var playlistsFolder = Path.Combine(nodeFolder, "Playlists");
            var safeTitle = SanitizeForFilename(item.Title);
            var fileName = $"{item.Id}_{safeTitle}.m3u";
            fullPath = Path.Combine(playlistsFolder, fileName);
            Directory.CreateDirectory(playlistsFolder);

            operation = "WritePlaylist";
            var candidatePath = Path.Combine(playlistsFolder, $".{fileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryPath = candidatePath;
                _writePlaylist(stream, lines);
                stream.Flush(flushToDisk: true);
            }

            // Same-directory rename publishes only a complete file; never truncate the old playlist.
            operation = "PublishPlaylist";
            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
            return fullPath;
        }
        catch (Exception ex)
        {
            failure = ex;
            return null;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    // Retain the original failure and report cleanup only as context on that report.
                    context["TemporaryPath"] = temporaryPath;
                    context["CleanupFailure"] = ex.GetType().Name;
                }
            }

            if (failure != null)
            {
                context["Operation"] = operation;
                if (fullPath != null)
                    context["PlaylistPath"] = fullPath;
                try
                {
                    _errorService.Report(new ErrorReport("Launcher.PlaylistCreationFailed", ErrorSeverity.Warning,
                        nameof(LaunchPlaylistService),
                        primary != null
                            ? "The multi-disc playlist could not be created. Launching the primary file instead; any previous playlist is not used."
                            : "The multi-disc playlist could not be created and no valid primary launch path is available.",
                        failure, Context: context));
                }
                catch
                {
                    // Diagnostic output must not change the launch fallback or prevent cleanup.
                }
            }
        }
    }

    private static bool IsValidEntry(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(['\r', '\n', '\0']) < 0;

    private static void WritePlaylist(Stream stream, IReadOnlyList<string> lines)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        foreach (var line in lines)
            writer.WriteLine(line);
    }

    private static string SanitizeForFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var sanitized = input.Replace(' ', '_');

        foreach (var character in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(character.ToString(), string.Empty);

        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        const int maxLength = 80;
        return sanitized.Length > maxLength ? sanitized[..maxLength] : sanitized;
    }
}
