using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Handles the execution of games and applications.
/// Supports native executables, emulators, and external launchers (Steam, GOG).
/// Also manages playtime tracking and Wine prefix creation for Linux compatibility.
/// </summary>
public class LauncherService
{
    // Minimum session time to be counted as "played" (avoids false positives on crash/mistake)
    private const int MinPlayTimeSeconds = 5; 

    // Der Root-Pfad der Bibliothek
    private readonly string _libraryRootPath;
    
    // Konstruktor Injection für den Pfad
    public LauncherService(string libraryRootPath)
    {
        _libraryRootPath = libraryRootPath;
    }
    
    /// <summary>
    /// Launches the specified media item.
    /// </summary>
    /// <param name="item">The media item to launch.</param>
    /// <param name="inheritedConfig">Optional emulator configuration inherited from parent nodes.</param>
    /// <param name="nodePath">The hierarchical path to the item (used for generating Wine prefix paths).</param>
    public async Task LaunchAsync(MediaItem item, EmulatorConfig? inheritedConfig = null, List<string>? nodePath = null)
    {
        if (item == null) return;
        
        Process? process = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // --- 1. START PROCESS ---
            
            if (item.MediaType == MediaType.Command)
            {
                process = LaunchCommand(item);
            }
            else
            {
                process = LaunchNativeOrEmulator(item, inheritedConfig, nodePath);
            }

            // --- 2. WATCH & TRACK ---

            // CASE A: Explicit process monitoring requested (e.g. for Steam games or wrapper scripts)
            if (!string.IsNullOrWhiteSpace(item.OverrideWatchProcess))
            {
                await WatchProcessByNameAsync(item.OverrideWatchProcess);
            }
            // CASE B: Standard process handle available (Emulator / Native)
            else if (process != null && !process.HasExited)
            {
                await process.WaitForExitAsync();
            }
            // CASE C: Command launched (e.g. Steam URL), but no process handle and no watch target.
            // We cannot track time here (Time ~0).
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Error during launch/tracking: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            EvaluateSession(item, stopwatch.Elapsed);
        }
    }

    private Process? LaunchCommand(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.LauncherArgs)) return null;

        return Process.Start(new ProcessStartInfo
        {
            FileName = item.FilePath ?? "xdg-open", // Fallback for Linux URLs
            Arguments = item.LauncherArgs,
            UseShellExecute = true
        });
    }

    private Process? LaunchNativeOrEmulator(MediaItem item, EmulatorConfig? inheritedConfig, List<string>? nodePath)
    {
        try
        {
            // Determine executable and arguments
            string fileName;
            string? templateArgs;

            if (!string.IsNullOrEmpty(item.LauncherPath))
            {
                // Custom Config on Item level
                fileName = item.LauncherPath;
                templateArgs = string.IsNullOrWhiteSpace(item.LauncherArgs) ? "{file}" : item.LauncherArgs;
            }
            else if (inheritedConfig != null)
            {
                // Inherited Emulator Config
                fileName = inheritedConfig.Path;
                templateArgs = inheritedConfig.Arguments;
            }
            else
            {
                // Native Execution
                if (string.IsNullOrEmpty(item.FilePath)) return null;
                fileName = item.FilePath;
                templateArgs = item.LauncherArgs;
            }

            var isNative = string.IsNullOrEmpty(item.LauncherPath) && inheritedConfig == null;

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = isNative
            };

            // Set Working Directory
            if (File.Exists(fileName))
                startInfo.WorkingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty;
            else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                startInfo.WorkingDirectory = Path.GetDirectoryName(item.FilePath) ?? string.Empty;

            // Setup Wine Prefix if needed (Linux specific)
            if (!isNative)
            {
                ConfigureWinePrefix(item, nodePath, startInfo);
            }

            // Build Arguments
            if (isNative)
            {
                startInfo.Arguments = templateArgs ?? "";
            }
            else
            {
                // We construct the full argument string manually to support complex templates
                // provided by the user (e.g., "-L core.dll \"{file}\"").
                // Using ArgumentList is safer generally, but parsing a user-provided template string
                // into a list correctly handles quotes is error-prone.
                startInfo.Arguments = BuildArgumentsString(item, templateArgs);
            }

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Failed to launch native/emulator: {ex.Message}");
            return null;
        }
    }

    private void ConfigureWinePrefix(MediaItem item, List<string>? nodePath, ProcessStartInfo startInfo)
    {
        string? prefixPath = null;
        string? relativePrefixPathToSave = null;

        // Priority 1: Existing saved path (Relative to Library Root)
        if (!string.IsNullOrEmpty(item.PrefixPath))
        {
            prefixPath = Path.Combine(_libraryRootPath, item.PrefixPath);
        }
        // Priority 2: Generate new path based on node structure
        else if (nodePath != null)
        {
            // Pfad aufbauen: Games/Windows/Prefixes/GameName
            var relPaths = new List<string>(nodePath) { "Prefixes" };
            
            var safeTitle = string.Join("_", item.Title.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
            relPaths.Add(safeTitle);

            relativePrefixPathToSave = Path.Combine(relPaths.ToArray());
            prefixPath = Path.Combine(_libraryRootPath, relativePrefixPathToSave);
        }
        // Priority 3: Fallback near ROM
        else if (!string.IsNullOrEmpty(item.FilePath))
        {
            var gameDir = Path.GetDirectoryName(item.FilePath);
            if (!string.IsNullOrEmpty(gameDir)) prefixPath = Path.Combine(gameDir, "pfx");
        }

        if (!string.IsNullOrEmpty(prefixPath))
        {
            if (!Directory.Exists(prefixPath)) Directory.CreateDirectory(prefixPath);
            startInfo.EnvironmentVariables["WINEPREFIX"] = prefixPath;

            // Persist the generated path to the item
            if (relativePrefixPathToSave != null) item.PrefixPath = relativePrefixPathToSave;
        }
    }

    private string BuildArgumentsString(MediaItem item, string? templateArgs)
    {
        var fullPath = string.IsNullOrEmpty(item.FilePath) ? "" : Path.GetFullPath(item.FilePath);

        // Ensure the path is quoted if it contains spaces and isn't already quoted in the template
        // However, the safest bet is to let the USER control quotes in the template OR
        // we force quotes around the replacement.
        
        // Strategy: We simply replace {file} with the path. 
        // If the path has spaces, we wrap it in quotes UNLESS the template already has quotes around {file}.
        
        string pathReplacement = fullPath;
        if (!string.IsNullOrEmpty(pathReplacement) && pathReplacement.Contains(" "))
        {
            pathReplacement = $"\"{pathReplacement}\"";
        }

        if (string.IsNullOrWhiteSpace(templateArgs))
        {
            // Default: just the file path (quoted if needed)
            return pathReplacement;
        }

        // Check if the user already provided quotes in the template, e.g. "-rom \"{file}\""
        // If so, we should NOT add extra quotes, otherwise we get ""path"".
        // Simplistic check:
        if (templateArgs.Contains("\"{file}\""))
        {
            return templateArgs.Replace("{file}", fullPath);
        }

        return templateArgs.Replace("{file}", pathReplacement);
    }

    private async Task WatchProcessByNameAsync(string processName)
    {
        var cleanName = Path.GetFileNameWithoutExtension(processName);
        Debug.WriteLine($"[Launcher] Watching for process: {cleanName}...");

        var startupTimeout = TimeSpan.FromMinutes(3);
        var startWatch = Stopwatch.StartNew();
        bool found = false;

        // Phase 1: Wait for process to APPEAR
        while (startWatch.Elapsed < startupTimeout)
        {
            var processes = Process.GetProcessesByName(cleanName);
            if (processes.Length > 0)
            {
                found = true;
                Debug.WriteLine("[Launcher] Process found! Tracking...");
                break;
            }
            await Task.Delay(1000);
        }

        if (!found)
        {
            Debug.WriteLine("[Launcher] Process not found (Timeout).");
            return;
        }

        // Phase 2: Wait for process to DISAPPEAR
        while (true)
        {
            await Task.Delay(2000);
            var processes = Process.GetProcessesByName(cleanName);
            if (processes.Length == 0)
            {
                Debug.WriteLine("[Launcher] Process exited.");
                break;
            }
        }
    }

    private void EvaluateSession(MediaItem item, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        Debug.WriteLine($"[Launcher] Session ended. Duration: {seconds:F2}s");

        if (seconds > MinPlayTimeSeconds)
        {
            UpdateStats(item, elapsed);
        }
        else if (item.MediaType == MediaType.Command && string.IsNullOrEmpty(item.OverrideWatchProcess))
        {
            // For untracked commands (Steam without explicit watch process), we count at least the start.
            UpdateStats(item, TimeSpan.Zero);
        }
        else
        {
            Debug.WriteLine("[Launcher] Session too short (ignored).");
        }
    }
    
    private void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }
}