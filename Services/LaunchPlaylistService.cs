using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Resolves the media path passed to a launcher and creates optional multi-disc playlists.
/// </summary>
public sealed class LaunchPlaylistService
{
    private readonly string _libraryRootPath;

    public LaunchPlaylistService(string libraryRootPath)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
    }

    public string? ResolveLaunchFilePath(
        MediaItem item,
        List<string>? nodePath,
        bool usePlaylistForMultiDisc)
    {
        var primary = item.GetPrimaryLaunchPath();
        if (string.IsNullOrWhiteSpace(primary))
            return null;

        if (!usePlaylistForMultiDisc || item.Files is not { Count: > 1 } || nodePath is not { Count: > 0 })
            return primary;

        var playlistPath = CreateOrUpdatePlaylist(item, nodePath);
        return string.IsNullOrWhiteSpace(playlistPath) ? primary : playlistPath;
    }

    private string? CreateOrUpdatePlaylist(MediaItem item, List<string> nodePath)
    {
        try
        {
            var nodeFolder = PathHelper.ResolveNodeFolder(nodePath, _libraryRootPath);
            var playlistsFolder = Path.Combine(nodeFolder, "Playlists");
            Directory.CreateDirectory(playlistsFolder);

            var safeTitle = SanitizeForFilename(item.Title);
            var fileName = $"{item.Id}_{safeTitle}.m3u";
            var fullPath = Path.Combine(playlistsFolder, fileName);

            var ordered = new List<MediaFileRef>(item.Files);
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
                if (string.IsNullOrWhiteSpace(file.Path))
                    continue;

                switch (file.Kind)
                {
                    case MediaFileKind.Absolute:
                        lines.Add(file.Path);
                        break;

                    case MediaFileKind.LibraryRelative:
                        var resolved = AppPaths.ResolveDataPathInsideRootOrEmpty(file.Path);
                        if (!string.IsNullOrWhiteSpace(resolved))
                            lines.Add(resolved);
                        break;
                }
            }

            if (lines.Count == 0)
                return null;

            File.WriteAllLines(fullPath, lines);
            return fullPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Failed to create playlist: {ex.Message}");
            return null;
        }
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
