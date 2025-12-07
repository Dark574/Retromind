using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Service responsible for importing installed games from external stores like Steam and Heroic (GOG/Epic).
/// </summary>
public class StoreImportService
{
    // --- Configuration ---

    // Known default paths for Steam libraries on Linux.
    // TODO: Add Windows/macOS paths for full cross-platform support.
    private readonly string[] _steamPaths = 
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam/steam/steamapps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps")
    };

    // Path for Heroic Launcher configuration (GOG store).
    private readonly string _heroicConfigPath = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/heroic/gog_store/installed.json");

    // Regex to parse Steam's ACF format lines: "key"		"value"
    // Captures the key in group 1 and the value in group 2.
    private static readonly Regex SteamAcfRegex = new("\"(.+?)\"\\s+\"(.+?)\"", RegexOptions.Compiled);

    /// <summary>
    /// Scans known Steam library folders for installed games.
    /// Filters out common runtime/tool entries (Proton, Steamworks).
    /// </summary>
    public async Task<List<MediaItem>> ImportSteamGamesAsync()
    {
        return await Task.Run(async () =>
        {
            var results = new List<MediaItem>();
            
            // Find the first existing Steam library path
            var foundPath = _steamPaths.FirstOrDefault(Directory.Exists);
            if (foundPath == null)
            {
                Debug.WriteLine("[StoreImport] No Steam library found.");
                return results;
            }

            try
            {
                // Steam uses appmanifest_*.acf files for installed games
                var acfFiles = Directory.GetFiles(foundPath, "appmanifest_*.acf");

                foreach (var file in acfFiles)
                {
                    var item = await ParseSteamAcfFileAsync(file);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StoreImport] Error scanning Steam folder: {ex.Message}");
            }

            return results;
        });
    }

    /// <summary>
    /// Parses a single Steam ACF manifest file.
    /// </summary>
    private async Task<MediaItem?> ParseSteamAcfFileAsync(string filePath)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            string? name = null;
            string? appId = null;

            foreach (var line in lines)
            {
                var match = SteamAcfRegex.Match(line);
                if (match.Success)
                {
                    var key = match.Groups[1].Value;
                    var value = match.Groups[2].Value;

                    if (key == "name") name = value;
                    else if (key == "appid") appId = value;
                }

                if (name != null && appId != null) break; // Found everything we need
            }

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId)) return null;

            // Filter internal tools
            if (name.Contains("Steamworks Common Redist") || 
                name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new MediaItem
            {
                Title = name,
                // Use the steam protocol to launch the game
                FilePath = "steam", 
                LauncherArgs = $"steam://rungameid/{appId}",
                MediaType = MediaType.Command,
                Description = "Imported from Steam",
                Developer = "Valve / Steam"
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreImport] Failed to parse ACF '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Imports games installed via Heroic Games Launcher (specifically GOG support).
    /// </summary>
    public async Task<List<MediaItem>> ImportHeroicGogAsync()
    {
        var results = new List<MediaItem>();

        if (!File.Exists(_heroicConfigPath))
        {
            Debug.WriteLine("[StoreImport] Heroic GOG config not found.");
            return results;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_heroicConfigPath);
            var rootNode = JsonSerializer.Deserialize<JsonNode>(json);
            
            // Heroic's JSON structure for installed.json:
            // { "installed": [ { "title": "...", "appName": "...", ... }, ... ] }
            
            if (rootNode is JsonObject obj && obj.TryGetPropertyValue("installed", out var installedNode))
            {
                var gamesList = installedNode?.AsArray();
                if (gamesList != null)
                {
                    foreach (var gameNode in gamesList)
                    {
                        var item = ParseHeroicGameNode(gameNode);
                        if (item != null)
                        {
                            results.Add(item);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreImport] Heroic Import Error: {ex.Message}");
        }

        return results;
    }

    private MediaItem? ParseHeroicGameNode(JsonNode? gameNode)
    {
        if (gameNode == null) return null;

        var title = gameNode["title"]?.ToString();
        var id = gameNode["appName"]?.ToString(); // GOG ID
        var platform = gameNode["platform"]?.ToString() ?? "Unknown";

        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(id)) return null;

        return new MediaItem
        {
            Title = title,
            // Command to launch via Heroic CLI
            FilePath = "heroic",
            LauncherArgs = $"gog://{id}",
            MediaType = MediaType.Command,
            Description = $"Imported from Heroic (GOG) - Platform: {platform}",
            Developer = "GOG"
        };
    }
}