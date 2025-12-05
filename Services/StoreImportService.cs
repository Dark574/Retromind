using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

public class StoreImportService
{
    // Linux paths for Steam
    private readonly string[] _steamPaths = 
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam/steam/steamapps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps")
    };

    // Linux path for Heroic (GOG/Epic)
    private readonly string _heroicConfigPath = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/heroic/gog_store/installed.json");

    public async Task<List<MediaItem>> ImportSteamGamesAsync()
    {
        var results = new List<MediaItem>();
        var foundPath = _steamPaths.FirstOrDefault(Directory.Exists);

        if (foundPath == null) return results;

        var acfFiles = Directory.GetFiles(foundPath, "appmanifest_*.acf");

        foreach (var file in acfFiles)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file);
                var nameLine = lines.FirstOrDefault(l => l.Contains("\"name\""));
                var idLine = lines.FirstOrDefault(l => l.Contains("\"appid\""));

                if (nameLine != null && idLine != null)
                {
                    var name = ExtractValue(nameLine);
                    var id = ExtractValue(idLine);

                    // Filter Proton/Linux specific entries (Steamworks Common Redist etc.)
                    if (name.Contains("Steamworks") || name.Contains("Proton")) continue;

                    results.Add(new MediaItem
                    {
                        Title = name,
                        // We store the Steam URL directly as "FilePath" or use LauncherArgs
                        // For Retromind we use MediaType.Command
                        FilePath = "steam", 
                        LauncherArgs = $"steam://rungameid/{id}",
                        MediaType = MediaType.Command,
                        Description = "Importiert von Steam",
                        Developer = "Steam"
                    });
                }
            }
            catch
            {
                // Ignore corrupt files
            }
        }

        return results;
    }

    public async Task<List<MediaItem>> ImportHeroicGogAsync()
    {
        var results = new List<MediaItem>();

        if (!File.Exists(_heroicConfigPath)) return results;

        try
        {
            var json = await File.ReadAllTextAsync(_heroicConfigPath);
            var data = JsonSerializer.Deserialize<JsonNode>(json);
            
            if (data is JsonObject obj)
            {
                // Heroic stores installed.json as a list of objects
                // { "installed": [ ... ] }
                var list = obj["installed"]?.AsArray();
                if (list != null)
                {
                    foreach (var game in list)
                    {
                        if (game == null) continue; 
                        var title = game["title"]?.ToString();
                        var id = game["appName"]?.ToString(); // GOG ID
                        var platform = game["platform"]?.ToString(); // "windows" oder "linux"
                        
                        if (title != null && id != null)
                        {
                            results.Add(new MediaItem
                            {
                                Title = title,
                                // Heroic CLI command: heroic "gog://ID"
                                FilePath = "heroic",
                                LauncherArgs = $"gog://{id}",
                                MediaType = MediaType.Command,
                                Description = $"Importiert von Heroic (GOG) - {platform}",
                                Developer = "GOG"
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Heroic Import Error: {ex.Message}");
        }

        return results;
    }

    // Helper method for Steam ACF format ("key" "value")
    private string ExtractValue(string line)
    {
        var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
        // parts[0] is key, parts[1] is value (usually)
        if (parts.Length >= 2) return parts.Last();
        return "Unknown";
    }
}