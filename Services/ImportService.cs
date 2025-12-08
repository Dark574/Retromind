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
/// Includes logic to handle multi-disc games intelligently.
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
            // Using StringComparer.OrdinalIgnoreCase avoids allocating new strings via .ToLower() inside the loop.
            var validExtensions = extensions
                .Select(e => e.Trim())
                .Select(e => e.StartsWith(".") ? e : "." + e)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Configure enumeration to skip system folders or folders without permission
            // instead of crashing or requiring a try-catch block inside the loop.
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                // Optional: Optimize buffer size if needed, but default is usually fine.
            };

            try
            {
                // EnumerateFiles is lazy; it yields files as they are found.
                var files = Directory.EnumerateFiles(sourceFolder, "*.*", enumOptions)
                    .OrderBy(f => f) // Ensure consistent order (Disk 1 comes before Disk 2)
                    .ToList();

                // HashSet to keep track of games we already added (cleaned titles)
                var addedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    // Performance Note: Path.GetExtension is efficient (span-based in newer .NET), 
                    // and our HashSet handles the case-insensitivity without extra allocations.
                    var ext = Path.GetExtension(file);

                    if (validExtensions.Contains(ext))
                    {
                        var originalTitle = Path.GetFileNameWithoutExtension(file);
                        string cleanTitle = originalTitle;
                        bool isFirstDisc = false;

                        // Check for Multi-Disc pattern
                        var match = MultiDiscRegex.Match(originalTitle);
                        if (match.Success)
                        {
                            // Remove the " (Disk 1)" part to get the clean game name
                            cleanTitle = originalTitle.Replace(match.Value, "").Trim();
                            
                            // Check if it is Disc 1 / Side A / Part 1
                            var discNumber = match.Groups[3].Value; // "1", "A"
                            isFirstDisc = discNumber == "1" || discNumber.Equals("A", StringComparison.OrdinalIgnoreCase);
                        }

                        // LOGIC:
                        // 1. If it's a normal game -> Add it.
                        // 2. If it's Disc 1 -> Add it with cleaned title.
                        // 3. If it's Disc 2+ -> Skip it (to keep library clean).
                        
                        // BUT: Check if we already added this title (e.g. from a different file format or messy naming)
                        if (addedTitles.Contains(cleanTitle))
                        {
                            // Already have "World Games", so skip "World Games (Disk 2)"
                            continue;
                        }

                        // If it's a multi-disc game, we only want to add it if it's the first disc OR 
                        // if our regex logic failed to identify it as Disc 2 (fallback).
                        // Since we ordered files alphabetically, Disk 1 comes first.
                        // So we simply add it to the Set. Future "Disk 2" will be blocked by Contains(cleanTitle).

                        addedTitles.Add(cleanTitle);

                        var item = new MediaItem
                        {
                            Title = cleanTitle, // Use clean title (e.g. "World Games")
                            FilePath = file,    // Points to Disc 1 file
                            MediaType = MediaType.Native 
                        };

                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log critical errors that stop the entire enumeration (e.g., sourceFolder not found)
                Debug.WriteLine($"[ImportService] Error importing from '{sourceFolder}': {ex.Message}");
            }

            return results;
        });
    }
}