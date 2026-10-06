using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
    private static readonly Regex CueFileReferenceRegex = new(
        "^\\s*FILE\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>.+?))\\s+\\S+\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Recursively scans a directory for files matching the specified extensions.
    /// Handles inaccessible directories gracefully and optimizes for large file counts.
    /// </summary>
    /// <param name="sourceFolder">The root directory path to scan.</param>
    /// <param name="extensions">List of file extensions to include (e.g., ".iso", "rom").</param>
    /// <returns>A list of created <see cref="MediaItem"/> objects.</returns>
    public Task<List<MediaItem>> ImportFromFolderAsync(
        string sourceFolder,
        string[] extensions,
        IProgress<FolderImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
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
                var selectedFiles = new List<string>();
                long filesScanned = 0;
                foreach (var file in Directory.EnumerateFiles(sourceFolder, "*.*", enumOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    filesScanned++;

                    if (validExtensions.Contains(Path.GetExtension(file)))
                        selectedFiles.Add(file);

                    if (filesScanned == 1 || filesScanned % 256 == 0)
                    {
                        progress?.Report(new FolderImportProgress(
                            FolderImportStage.ScanningFiles,
                            filesScanned,
                            selectedFiles.Count));
                    }
                }

                progress?.Report(new FolderImportProgress(
                    FolderImportStage.ScanningFiles,
                    filesScanned,
                    selectedFiles.Count));
                cancellationToken.ThrowIfCancellationRequested();

                selectedFiles.Sort(static (left, right) =>
                {
                    var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
                });

                var referencedCuePayloads = FindReferencedCuePayloads(
                    selectedFiles,
                    progress,
                    cancellationToken);

                // Step 1: collect launchable candidates and compute grouping keys + disc metadata.
                // A CUE file is the launch descriptor; referenced BIN/IMG track files are payload only.
                var candidates = new List<(string GroupingKey, string CleanTitle, string FullPath, int? Index, string? Label)>(
                    capacity: Math.Min(selectedFiles.Count, 4096));

                for (var fileIndex = 0; fileIndex < selectedFiles.Count; fileIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = selectedFiles[fileIndex];
                    if (referencedCuePayloads.Contains(file))
                        continue;

                    var originalTitle = Path.GetFileNameWithoutExtension(file);
                    var (cleanTitle, discIndex, discLabel) =
                        MultiDiscFileNameHelper.Parse(originalTitle);
                    var groupingKey = MultiDiscFileNameHelper.GetGroupingKey(cleanTitle);

                    candidates.Add((groupingKey, cleanTitle, file, discIndex, discLabel));

                    if (fileIndex == 0 || (fileIndex + 1) % 256 == 0)
                    {
                        progress?.Report(new FolderImportProgress(
                            FolderImportStage.PreparingItems,
                            filesScanned,
                            selectedFiles.Count,
                            fileIndex + 1,
                            selectedFiles.Count));
                    }
                }

                progress?.Report(new FolderImportProgress(
                    FolderImportStage.PreparingItems,
                    filesScanned,
                    selectedFiles.Count,
                    selectedFiles.Count,
                    selectedFiles.Count));
                cancellationToken.ThrowIfCancellationRequested();

                // Step 2: group by clean title -> one MediaItem per game
                var groups = candidates
                    .GroupBy(c => c.GroupingKey, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var g in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImportService] Error importing from '{sourceFolder}': {ex.Message}");
            }

            return results;
        }, cancellationToken);
    }

    private static HashSet<string> FindReferencedCuePayloads(
        IReadOnlyCollection<string> selectedFiles,
        IProgress<FolderImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectedPaths = selectedFiles.ToHashSet(FileSystemPathIdentity.Comparer);
        var referencedPaths = new HashSet<string>(FileSystemPathIdentity.Comparer);
        var cueCount = selectedFiles.Count(path =>
            path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        var processedCueCount = 0;

        foreach (var cuePath in selectedFiles.Where(path =>
                     path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var cueDirectory = Path.GetDirectoryName(cuePath) ?? string.Empty;
                foreach (var line in File.ReadLines(cuePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var match = CueFileReferenceRegex.Match(line);
                    if (!match.Success)
                        continue;

                    var referencedName = match.Groups["quoted"].Success
                        ? match.Groups["quoted"].Value
                        : match.Groups["plain"].Value.Trim();
                    referencedName = referencedName
                        .Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar);

                    var referencedPath = Path.GetFullPath(Path.Combine(cueDirectory, referencedName));
                    if (selectedPaths.Contains(referencedPath))
                        referencedPaths.Add(referencedPath);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A malformed or unreadable CUE must not abort the remaining bulk import.
            }

            processedCueCount++;
            progress?.Report(new FolderImportProgress(
                FolderImportStage.ReadingCueFiles,
                MatchingFileCount: selectedFiles.Count,
                CompletedCount: processedCueCount,
                TotalCount: cueCount));
        }

        return referencedPaths;
    }
}
