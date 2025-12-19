using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for scanning directories and importing media files.
/// Includes logic to handle multi-disc games by grouping files into a single MediaItem.
/// </summary>
public class ImportService
{
    // Regex to detect Disk/Disc/Side/Part suffixes
    // Matches: " (Disk 1)", "_Disk1", " (Side A)", " - CD 1", etc.
    private static readonly Regex MultiDiscRegex = new Regex(
        @"[\s_]*(\(?-?|\[)(Disk|Disc|CD|Side|Part)[\s_]*([0-9A-H]+)(\)?|\])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Recursively scans a directory for files matching the specified extensions.
    /// Handles inaccessible directories gracefully and optimizes for large file counts.
    /// </summary>
    /// <param name="sourceFolder">The root directory path to scan.</param>
    /// <param name="extensions">List of file extensions to include (e.g., ".iso", "rom").</param>
    /// <returns>A list of created <see cref="MediaItem"/> objects.</returns>
    public async Task<List<MediaItem>> ImportFromFolderAsync(string sourceFolder, string[] extensions)
    {
        return await Task.Run(() =>
        {
            var results = new List<MediaItem>();

            // Normalize extensions: ensure they start with '.' and use a case-insensitive HashSet for O(1) lookups.
            var validExtensions = extensions
                .Select(e => e.Trim())
                .Select(e => e.StartsWith(".") ? e : "." + e)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            };

            try
            {
                var files = Directory.EnumerateFiles(sourceFolder, "*.*", enumOptions)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Step 1: collect import candidates and compute grouping keys + disc metadata
                var candidates = new List<(string CleanTitle, string FullPath, int? Index, string? Label)>(
                    capacity: Math.Min(files.Count, 4096));

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    if (!validExtensions.Contains(ext))
                        continue;

                    var originalTitle = Path.GetFileNameWithoutExtension(file);
                    var cleanTitle = originalTitle.Trim();

                    int? discIndex = null;
                    string? discLabel = null;

                    var match = MultiDiscRegex.Match(originalTitle);
                    if (match.Success)
                    {
                        // Remove the suffix to get the clean game name
                        cleanTitle = originalTitle.Replace(match.Value, "").Trim();

                        // Extract disc indicator ("1", "2", "A", "B", ...)
                        var token = match.Groups[3].Value.Trim();

                        discIndex = ParseDiscIndex(token);
                        discLabel = BuildDiscLabel(match.Groups[2].Value, token, discIndex);
                    }

                    candidates.Add((cleanTitle, file, discIndex, discLabel));
                }

                // Step 2: group by clean title -> one MediaItem per game
                var groups = candidates
                    .GroupBy(c => c.CleanTitle, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var g in groups)
                {
                    var orderedFiles = g
                        .OrderBy(c => c.Index ?? int.MaxValue)
                        .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var item = new MediaItem
                    {
                        Title = g.Key,
                        MediaType = MediaType.Native,
                        Files = orderedFiles.Select(c => new MediaFileRef
                        {
                            Kind = MediaFileKind.Absolute,
                            Path = c.FullPath,
                            Index = c.Index,
                            Label = c.Label
                        }).ToList()
                    };

                    // Ensure we always have a stable primary entry (Disc 1 / first file)
                    if (item.Files.Count > 0 && item.Files.All(f => !f.Index.HasValue))
                    {
                        item.Files[0].Index = 1;
                        item.Files[0].Label ??= "Disc 1";
                    }

                    results.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImportService] Error importing from '{sourceFolder}': {ex.Message}");
            }

            return results;
        });
    }

    private static int? ParseDiscIndex(string token)
    {
        // Supports: "1", "2", ... and "A".."H" (Side A/B, etc.)
        if (int.TryParse(token, out var n) && n > 0)
            return n;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'H')
                return (c - 'A') + 1;
        }

        return null;
    }

    private static string? BuildDiscLabel(string kind, string token, int? index)
    {
        // Keep labels user-friendly and stable for UI and playlist readability.
        // Examples:
        // - "Disk 1" / "Disc 2" / "CD 1"
        // - "Side A" -> index 1, label "Side A"
        // - "Part 3" -> label "Part 3"
        kind = kind.Trim();

        if (string.Equals(kind, "Side", StringComparison.OrdinalIgnoreCase) && token.Length == 1)
        {
            var side = char.ToUpperInvariant(token[0]);
            if (side is >= 'A' and <= 'H')
                return $"Side {side}";
        }

        if (!string.IsNullOrWhiteSpace(token))
            return $"{kind} {token}";

        if (index.HasValue)
            return $"{kind} {index.Value}";

        return null;
    }
}