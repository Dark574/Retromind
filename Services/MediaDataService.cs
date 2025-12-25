using System;
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
    /// </summary>
    public async Task SaveAsync(ObservableCollection<MediaNode> nodes)
    {
        try
        {
            #if DEBUG
            var options = new JsonSerializerOptions { WriteIndented = true };
            #else
            var options = new JsonSerializerOptions { WriteIndented = false };
            #endif
            
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
                return new ObservableCollection<MediaNode>
                {
                    new(Strings.Media_Library, NodeType.Area)
                };
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
}