using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for scanning directories and importing media files.
/// Includes logic to handle multi-disc games by grouping files into a single MediaItem.
/// </summary>
public class ImportService
{
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
                var candidates = new List<(string GroupingKey, string CleanTitle, string FullPath, int? Index, string? Label)>(
                    capacity: Math.Min(files.Count, 4096));

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    if (!validExtensions.Contains(ext))
                        continue;

                    var originalTitle = Path.GetFileNameWithoutExtension(file);
                    var (cleanTitle, discIndex, discLabel) =
                        MultiDiscFileNameHelper.Parse(originalTitle);
                    var groupingKey = MultiDiscFileNameHelper.GetGroupingKey(cleanTitle);

                    candidates.Add((groupingKey, cleanTitle, file, discIndex, discLabel));
                }

                // Step 2: group by clean title -> one MediaItem per game
                var groups = candidates
                    .GroupBy(c => c.GroupingKey, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var g in groups)
                {
                    var orderedFiles = g
                        .OrderBy(c => c.Index ?? int.MaxValue)
                        .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var displayTitle = g
                        .Select(c => c.CleanTitle)
                        .FirstOrDefault(title =>
                            string.Equals(title, g.Key, StringComparison.OrdinalIgnoreCase))
                        ?? orderedFiles[0].CleanTitle;

                    var item = new MediaItem
                    {
                        Title = displayTitle,
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
}
