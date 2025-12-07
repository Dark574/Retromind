using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for scanning directories and importing media files.
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
                var files = Directory.EnumerateFiles(sourceFolder, "*.*", enumOptions);

                foreach (var file in files)
                {
                    // Performance Note: Path.GetExtension is efficient (span-based in newer .NET), 
                    // and our HashSet handles the case-insensitivity without extra allocations.
                    var ext = Path.GetExtension(file);

                    if (validExtensions.Contains(ext))
                    {
                        var title = Path.GetFileNameWithoutExtension(file);
                        var item = new MediaItem
                        {
                            Title = title,
                            FilePath = file,
                            MediaType = MediaType.Native // Logic to determine specific emulator could be added here
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