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
    // Linux Pfade für Steam
    private readonly string[] _steamPaths = 
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam/steam/steamapps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps")
    };

    // Linux Pfad für Heroic (GOG/Epic)
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

                    // Proton/Linux spezifische Dinge filtern (Steamworks Common Redist etc.)
                    if (name.Contains("Steamworks") || name.Contains("Proton")) continue;

                    results.Add(new MediaItem
                    {
                        Title = name,
                        // Wir speichern die Steam-URL direkt als "FilePath" oder nutzen LauncherArgs
                        // Für Retromind nutzen wir MediaType.Command
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
                // Heroic speichert installed.json als Liste von Objekten
                // { "installed": [ ... ] }
                var list = obj["installed"]?.AsArray();
                if (list != null)
                {
                    foreach (var game in list)
                    {
                        var title = game["title"]?.ToString();
                        var id = game["appName"]?.ToString(); // GOG ID
                        var platform = game["platform"]?.ToString(); // "windows" oder "linux"
                        
                        if (title != null && id != null)
                        {
                            results.Add(new MediaItem
                            {
                                Title = title,
                                // Heroic CLI Befehl: heroic "gog://ID"
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

    // Hilfsmethode für Steam ACF Format ("key" "value")
    private string ExtractValue(string line)
    {
        var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
        // parts[0] ist key, parts[1] ist value (meistens)
        if (parts.Length >= 2) return parts.Last();
        return "Unknown";
    }
}