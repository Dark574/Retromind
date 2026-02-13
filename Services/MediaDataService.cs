using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources; 

namespace Retromind.Services;

/// <summary>
/// Manages the persistence of the media library tree.
/// Handles loading, saving, and backup restoration of the JSON database.
/// </summary>
public class MediaDataService
{
    private const string FileName = "retromind_tree.json";
    private const string BackupFileName = "retromind_tree.bak";
    private const string TempFileName = "retromind_tree.tmp";

    // Store in writable DataRoot (AppImage-safe)
    private string FilePath => Path.Combine(AppPaths.DataRoot, FileName);
    private string BackupPath => Path.Combine(AppPaths.DataRoot, BackupFileName);
    private string TempPath => Path.Combine(AppPaths.DataRoot, TempFileName);

    // Serialize library IO to avoid concurrent temp/backup/replace races.
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    
    /// <summary>
    /// Saves the current state of the library to disk asynchronously.
    /// Uses an atomic write strategy (Write to Temp -> Move to Final) to prevent data corruption.
    /// All save operations are serialized via _ioGate to avoid concurrent file access.
    /// </summary>
    public async Task SaveAsync(ObservableCollection<MediaNode> nodes)
    {
        // Ensure only one save/load operation accesses the JSON files at a time.
        await _ioGate.WaitAsync().ConfigureAwait(false);
        
        try
        {
            var options = CreateSerializerOptions();

            Directory.CreateDirectory(AppPaths.DataRoot);

            // 1. Write to a temporary file first
            using (var stream = File.Create(TempPath))
            {
                await JsonSerializer.SerializeAsync(stream, nodes, options);
            }

            // 2. Create a backup of the existing valid file
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }
                catch
                {
                    // best effort
                }
            }

            // 3. Atomic Move: Replace the real file with the temp file
            // This operation is atomic on most file systems (file is either there or not, no half-written states)
            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaDataService] Save failed: {ex.Message}");
            
            // Cleanup temp file if something went wrong
            try
            {
                if (File.Exists(TempPath)) File.Delete(TempPath);
            }
            catch
            {
                // ignore
            }

            // Optional: Try to restore backup if the main file got lost during the move (rare edge case)
            try
            {
                if (!File.Exists(FilePath) && File.Exists(BackupPath))
                {
                    File.Copy(BackupPath, FilePath, overwrite: true);
                }
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Serializes the given nodes to JSON using the same options as SaveAsync.
    /// Call this on a snapshot to avoid cross-thread collection access.
    /// </summary>
    public string Serialize(ObservableCollection<MediaNode> nodes)
    {
        var options = CreateSerializerOptions();
        return JsonSerializer.Serialize(nodes, options);
    }

    /// <summary>
    /// Creates a detached snapshot of the library tree.
    /// This should be called on the UI thread to avoid cross-thread access.
    /// </summary>
    public ObservableCollection<MediaNode> CreateSnapshot(ObservableCollection<MediaNode> nodes)
    {
        var snapshot = new ObservableCollection<MediaNode>();
        if (nodes == null) return snapshot;

        foreach (var node in nodes)
            snapshot.Add(CloneNode(node));

        return snapshot;
    }

    /// <summary>
    /// Saves a pre-serialized JSON snapshot to disk asynchronously using
    /// the same atomic write strategy as SaveAsync.
    /// </summary>
    public async Task SaveJsonAsync(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);

            // 1) Write to temp first
            await File.WriteAllTextAsync(TempPath, json).ConfigureAwait(false);

            // 2) Backup current file (best effort)
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }
                catch
                {
                    // best effort
                }
            }

            // 3) Atomic replace
            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaDataService] Save failed: {ex.Message}");

            // Cleanup temp file if something went wrong
            try
            {
                if (File.Exists(TempPath)) File.Delete(TempPath);
            }
            catch
            {
                // ignore
            }

            // Optional: Try to restore backup if the main file got lost during the move
            try
            {
                if (!File.Exists(FilePath) && File.Exists(BackupPath))
                {
                    File.Copy(BackupPath, FilePath, overwrite: true);
                }
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Loads the library from disk. 
    /// Attempts to load the main file first, then falls back to the backup if corruption is detected.
    /// </summary>
    /// <returns>The media tree or a new default tree if nothing exists.</returns>
    public async Task<ObservableCollection<MediaNode>> LoadAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);

            // Cleanup stale temp file (e.g. after crash during SaveAsync)
            try
            {
                if (File.Exists(TempPath))
                    File.Delete(TempPath);
            }
            catch
            {
                // ignore
            }

            ObservableCollection<MediaNode>? result = null;

            // 1. Try loading the main file
            if (File.Exists(FilePath))
            {
                try
                {
                    using var stream = File.OpenRead(FilePath);
                    result = await JsonSerializer.DeserializeAsync<ObservableCollection<MediaNode>>(stream).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"[MediaDataService] CRITICAL: Main DB corrupt: {ex.Message}");

                    // Quarantine the corrupt file for manual inspection
                    try
                    {
                        var corruptPath = FilePath + ".corrupt-" + DateTime.Now.Ticks;
                        File.Move(FilePath, corruptPath, overwrite: true);
                    }
                    catch
                    {
                        // ignore
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MediaDataService] General Load Error: {ex.Message}");
                }
            }

            // 2. If main file failed or didn't exist, try backup
            if (result == null || result.Count == 0)
            {
                if (File.Exists(BackupPath))
                {
                    Console.WriteLine("[MediaDataService] Attempting to restore from backup...");
                    result = await LoadFromFileAsync(BackupPath).ConfigureAwait(false);
                }
            }

            // 3. If everything failed, return a fresh tree
            if (result == null || result.Count == 0)
            {
                return new ObservableCollection<MediaNode>();
            }

            return result;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<ObservableCollection<MediaNode>?> LoadFromFileAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ObservableCollection<MediaNode>>(stream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaDataService] Failed to load from {path}: {ex.Message}");
            return null;
        }
    }

    private static MediaNode CloneNode(MediaNode node)
    {
        var clone = new MediaNode
        {
            Id = node.Id,
            Name = node.Name,
            Type = node.Type,
            IsExpanded = node.IsExpanded,
            RandomizeCovers = node.RandomizeCovers,
            RandomizeMusic = node.RandomizeMusic,
            DefaultEmulatorId = node.DefaultEmulatorId,
            ThemePath = node.ThemePath,
            Description = node.Description,
            SystemPreviewThemeId = node.SystemPreviewThemeId,
            LogoFallbackEnabled = node.LogoFallbackEnabled,
            WallpaperFallbackEnabled = node.WallpaperFallbackEnabled,
            VideoFallbackEnabled = node.VideoFallbackEnabled,
            MarqueeFallbackEnabled = node.MarqueeFallbackEnabled,
            NativeWrappersOverride = CloneWrappers(node.NativeWrappersOverride),
            EnvironmentOverrides = CloneEnvironmentOverridesOrNull(node.EnvironmentOverrides)
        };

        var assets = new ObservableCollection<MediaAsset>();
        if (node.Assets != null)
        {
            foreach (var asset in node.Assets)
                assets.Add(CloneAsset(asset));
        }
        clone.Assets = assets;

        var items = new ObservableCollection<MediaItem>();
        if (node.Items != null)
        {
            foreach (var item in node.Items)
                items.Add(CloneItem(item));
        }
        clone.Items = items;

        var children = new ObservableCollection<MediaNode>();
        if (node.Children != null)
        {
            foreach (var child in node.Children)
                children.Add(CloneNode(child));
        }
        clone.Children = children;

        return clone;
    }

    private static MediaItem CloneItem(MediaItem item)
    {
        var clone = new MediaItem
        {
            Id = item.Id,
            Title = item.Title,
            Files = CloneFiles(item.Files),
            MediaType = item.MediaType,
            Description = item.Description,
            Developer = item.Developer,
            Genre = item.Genre,
            Series = item.Series,
            Players = item.Players,
            ReleaseDate = item.ReleaseDate,
            Rating = item.Rating,
            Status = item.Status,
            IsFavorite = item.IsFavorite,
            EmulatorId = item.EmulatorId,
            LauncherPath = item.LauncherPath,
            LauncherArgs = item.LauncherArgs,
            WorkingDirectory = item.WorkingDirectory,
            XdgConfigPath = item.XdgConfigPath,
            XdgDataPath = item.XdgDataPath,
            XdgCachePath = item.XdgCachePath,
            XdgStatePath = item.XdgStatePath,
            XdgBasePath = item.XdgBasePath,
            PrefixPath = item.PrefixPath,
            WineArchOverride = item.WineArchOverride,
            OverrideWatchProcess = item.OverrideWatchProcess,
            LastPlayed = item.LastPlayed,
            PlayCount = item.PlayCount,
            TotalPlayTime = item.TotalPlayTime,
            NativeWrappersOverride = CloneWrappers(item.NativeWrappersOverride),
            EnvironmentOverrides = CloneEnvironmentOverrides(item.EnvironmentOverrides)
        };

        var tags = new ObservableCollection<string>();
        if (item.Tags != null)
        {
            foreach (var tag in item.Tags)
                tags.Add(tag);
        }
        clone.Tags = tags;

        var assets = new ObservableCollection<MediaAsset>();
        if (item.Assets != null)
        {
            foreach (var asset in item.Assets)
                assets.Add(CloneAsset(asset));
        }
        clone.Assets = assets;

        return clone;
    }

    private static MediaAsset CloneAsset(MediaAsset asset)
    {
        return new MediaAsset
        {
            Id = asset.Id,
            Type = asset.Type,
            RelativePath = asset.RelativePath
        };
    }

    private static List<MediaFileRef> CloneFiles(List<MediaFileRef>? files)
    {
        var cloned = new List<MediaFileRef>();
        if (files == null) return cloned;

        foreach (var file in files)
        {
            if (file == null) continue;
            cloned.Add(new MediaFileRef
            {
                Kind = file.Kind,
                Path = file.Path,
                Label = file.Label,
                Index = file.Index
            });
        }

        return cloned;
    }

    private static List<LaunchWrapper>? CloneWrappers(List<LaunchWrapper>? wrappers)
    {
        if (wrappers == null) return null;

        var cloned = new List<LaunchWrapper>(wrappers.Count);
        foreach (var wrapper in wrappers)
        {
            if (wrapper == null) continue;
            cloned.Add(new LaunchWrapper
            {
                Path = wrapper.Path,
                Args = wrapper.Args
            });
        }

        return cloned;
    }

    private static Dictionary<string, string> CloneEnvironmentOverrides(Dictionary<string, string>? overrides)
    {
        var cloned = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overrides == null) return cloned;

        foreach (var kv in overrides)
            cloned[kv.Key] = kv.Value;
        return cloned;
    }

    private static Dictionary<string, string>? CloneEnvironmentOverridesOrNull(Dictionary<string, string>? overrides)
    {
        if (overrides == null)
            return null;

        var cloned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in overrides)
            cloned[kv.Key] = kv.Value;
        return cloned;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions { WriteIndented = true };
    }
}
