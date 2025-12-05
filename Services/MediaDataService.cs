using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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

    // Path to the data file in the application directory
    private string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
    private string BackupPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BackupFileName);
    private string TempPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TempFileName);

    /// <summary>
    /// Saves the current state of the library to disk asynchronously.
    /// Uses an atomic write strategy (Write to Temp -> Move to Final) to prevent data corruption.
    /// </summary>
    public async Task SaveAsync(ObservableCollection<MediaNode> nodes)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };

            // 1. Write to a temporary file first
            using (var stream = File.Create(TempPath))
            {
                await JsonSerializer.SerializeAsync(stream, nodes, options);
            }

            // 2. Create a backup of the existing valid file
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, BackupPath, overwrite: true);
            }

            // 3. Atomic Move: Replace the real file with the temp file
            // This operation is atomic on most file systems (file is either there or not, no half-written states)
            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MediaDataService] Save failed: {ex.Message}");
            
            // Cleanup temp file if something went wrong
            if (File.Exists(TempPath)) File.Delete(TempPath);
            
            // Optional: Try to restore backup if the main file got lost during the move (rare edge case)
            if (!File.Exists(FilePath) && File.Exists(BackupPath))
            {
                File.Copy(BackupPath, FilePath);
            }
        }
    }

    /// <summary>
    /// Loads the library from disk. 
    /// Attempts to load the main file first, then falls back to the backup if corruption is detected.
    /// </summary>
    /// <returns>The media tree or a new default tree if nothing exists.</returns>
    public async Task<ObservableCollection<MediaNode>> LoadAsync()
    {
        ObservableCollection<MediaNode>? result = null;

        // 1. Try loading the main file
        if (File.Exists(FilePath))
        {
            try
            {
                using var stream = File.OpenRead(FilePath);
                result = await JsonSerializer.DeserializeAsync<ObservableCollection<MediaNode>>(stream);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[MediaDataService] CRITICAL: Main DB corrupt: {ex.Message}");
                
                // Quarantine the corrupt file for manual inspection
                var corruptPath = FilePath + ".corrupt-" + DateTime.Now.Ticks;
                File.Move(FilePath, corruptPath);
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
                result = await LoadFromFileAsync(BackupPath);
            }
        }

        // 3. If everything failed, return a fresh tree
        if (result == null || result.Count == 0)
        {
            return new ObservableCollection<MediaNode>
            {
                // Default root node
                new(Strings.Library, NodeType.Area) 
            };
        }

        return result;
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