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
    private const string PasswdPath = "/etc/passwd";

    private readonly AppSettings _settings;

    // --- Configuration ---

    // Known default paths for Steam libraries on Linux.
    private readonly string[] _steamPathSuffixes = 
    {
        Path.Combine(".steam", "steam", "steamapps"),
        Path.Combine(".local", "share", "Steam", "steamapps"),
        Path.Combine(".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps")
    };

    // Path suffix for Heroic Launcher configuration (GOG store).
    private readonly string _heroicGogConfigSuffix =
        Path.Combine(".config", "heroic", "gog_store", "installed.json");

    // Regex to parse Steam's ACF format lines: "key"		"value"
    // Captures the key in group 1 and the value in group 2.
    private static readonly Regex SteamAcfRegex = new("\"(.+?)\"\\s+\"(.+?)\"", RegexOptions.Compiled);
    private static readonly Regex SteamLibraryPathRegex = new("\"path\"\\s+\"(.+?)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SteamLibraryIndexRegex = new("\"\\d+\"\\s+\"(.+?)\"", RegexOptions.Compiled);

    public StoreImportService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Scans known Steam library folders for installed games.
    /// Filters out common runtime/tool entries (Proton, Steamworks).
    /// </summary>
    public async Task<List<MediaItem>> ImportSteamGamesAsync(
        string? manualLibraryPath = null,
        ICollection<string>? discoveredSteamAppsPaths = null)
    {
        return await Task.Run(async () =>
        {
            var results = new List<MediaItem>();
            
            var libraryPaths = await GetSteamLibraryPathsAsync(manualLibraryPath);
            if (libraryPaths.Count == 0)
            {
                Debug.WriteLine("[StoreImport] No Steam library found.");
                return results;
            }

            if (discoveredSteamAppsPaths != null)
            {
                foreach (var path in libraryPaths)
                    discoveredSteamAppsPaths.Add(path);
            }

            try
            {
                var seenAppIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var libraryPath in libraryPaths)
                {
                    // Steam uses appmanifest_*.acf files for installed games
                    var acfFiles = Directory.GetFiles(libraryPath, "appmanifest_*.acf");

                    foreach (var file in acfFiles)
                    {
                        var (item, appId) = await ParseSteamAcfFileAsync(file);
                        if (item != null && !string.IsNullOrWhiteSpace(appId))
                        {
                            if (!seenAppIds.Add(appId))
                                continue;
                            results.Add(item);
                        }
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

    private async Task<List<string>> GetSteamLibraryPathsAsync(string? manualLibraryPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manualSteamApps = ResolveSteamAppsPath(manualLibraryPath);
        if (!string.IsNullOrWhiteSpace(manualSteamApps) && Directory.Exists(manualSteamApps))
            paths.Add(Path.GetFullPath(manualSteamApps));

        if (_settings.SteamLibraryPaths is { Count: > 0 })
        {
            foreach (var storedPath in _settings.SteamLibraryPaths)
            {
                var resolved = ResolveSteamAppsPath(storedPath);
                if (!string.IsNullOrWhiteSpace(resolved) && Directory.Exists(resolved))
                    paths.Add(Path.GetFullPath(resolved));
            }
        }

        foreach (var steamAppsPath in GetSteamAppsCandidatePaths())
        {
            if (Directory.Exists(steamAppsPath))
            {
                paths.Add(Path.GetFullPath(steamAppsPath));
            }
        }

        // Read libraryfolders.vdf for additional libraries (new + old formats).
        foreach (var steamAppsPath in paths.ToList())
        {
            var vdfPath = Path.Combine(steamAppsPath, "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) continue;

            var libraryRoots = await ParseSteamLibraryFoldersAsync(vdfPath);
            foreach (var root in libraryRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var steamApps = root.EndsWith("steamapps", StringComparison.OrdinalIgnoreCase)
                    ? root
                    : Path.Combine(root, "steamapps");

                if (Directory.Exists(steamApps))
                {
                    paths.Add(Path.GetFullPath(steamApps));
                }
            }
        }

        return paths.ToList();
    }

    private IEnumerable<string> GetSteamAppsCandidatePaths()
    {
        foreach (var home in GetHomeCandidates())
        {
            foreach (var suffix in _steamPathSuffixes)
                yield return Path.Combine(home, suffix);
        }
    }

    private static string? ResolveSteamAppsPath(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return null;

        var fullPath = Path.GetFullPath(inputPath);
        if (!Directory.Exists(fullPath))
            return null;

        var name = Path.GetFileName(fullPath);
        if (string.Equals(name, "steamapps", StringComparison.OrdinalIgnoreCase))
            return fullPath;

        var candidate = Path.Combine(fullPath, "steamapps");
        if (Directory.Exists(candidate))
            return candidate;

        if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(fullPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) &&
                string.Equals(Path.GetFileName(parent), "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return parent;
            }
        }

        try
        {
            if (Directory.GetFiles(fullPath, "appmanifest_*.acf").Length > 0)
                return fullPath;
        }
        catch
        {
            // Ignore invalid or inaccessible folders.
        }

        return null;
    }

    private IEnumerable<string> GetHomeCandidates()
    {
        var currentHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentHome))
            yield return currentHome;

        if (!ShouldAddRealHomeFallback())
            yield break;

        var realHome = TryGetRealUserHomePath();
        if (string.IsNullOrWhiteSpace(realHome))
            yield break;

        if (!string.Equals(currentHome, realHome, StringComparison.OrdinalIgnoreCase))
            yield return realHome;
    }

    private bool ShouldAddRealHomeFallback()
    {
        if (!_settings.UsePortableHomeInAppImage)
            return false;

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        return !string.IsNullOrWhiteSpace(appImage) || !string.IsNullOrWhiteSpace(appDir);
    }

    private static string? TryGetRealUserHomePath()
    {
        try
        {
            var userName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(userName))
                return null;

            if (!File.Exists(PasswdPath))
                return null;

            foreach (var line in File.ReadLines(PasswdPath))
            {
                if (!line.StartsWith(userName + ":", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(':');
                if (parts.Length > 5)
                    return parts[5];
            }
        }
        catch
        {
            // Best-effort: if we can't read the real home, just skip it.
        }

        return null;
    }

    private async Task<List<string>> ParseSteamLibraryFoldersAsync(string vdfPath)
    {
        var results = new List<string>();
        var lines = await File.ReadAllLinesAsync(vdfPath);
        var hasPathEntries = lines.Any(line => line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase));

        foreach (var line in lines)
        {
            if (hasPathEntries)
            {
                var match = SteamLibraryPathRegex.Match(line);
                if (!match.Success) continue;
                var value = UnescapeVdfValue(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(value))
                    results.Add(value);
            }
            else
            {
                var match = SteamLibraryIndexRegex.Match(line);
                if (!match.Success) continue;
                var value = UnescapeVdfValue(match.Groups[1].Value);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Contains('/') || value.Contains('\\') || value.Contains(':'))
                    results.Add(value);
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string UnescapeVdfValue(string value)
    {
        return value.Replace("\\\\", "\\").Replace("\\\"", "\"");
    }

    /// <summary>
    /// Parses a single Steam ACF manifest file.
    /// </summary>
    private async Task<(MediaItem? Item, string? AppId)> ParseSteamAcfFileAsync(string filePath)
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

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId))
                return (null, null);

            // Filter internal tools
            if (name.Contains("Steamworks Common Redist") || 
                name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase))
            {
                return (null, null);
            }

            return (new MediaItem
            {
                Title = name,
                // Use the steam command to launch the game (Steam will handle the URL argument).
                Files = new List<MediaFileRef>
                {
                    new()
                    {
                        Kind = MediaFileKind.Absolute,
                        Path = "steam",
                        Index = 1,
                        Label = "Steam"
                    }
                },
                LauncherArgs = $"steam://rungameid/{appId}",
                MediaType = MediaType.Command,
                Description = "Imported from Steam",
                Developer = "Valve / Steam"
            }, appId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreImport] Failed to parse ACF '{filePath}': {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Imports games installed via Heroic Games Launcher (specifically GOG support).
    /// </summary>
    public async Task<List<MediaItem>> ImportHeroicGogAsync(
        string? manualConfigPath = null,
        ICollection<string>? discoveredConfigPaths = null)
    {
        var results = new List<MediaItem>();

        var configPaths = GetHeroicGogConfigPaths(manualConfigPath);
        if (configPaths.Count == 0)
        {
            Debug.WriteLine("[StoreImport] Heroic GOG config not found.");
            return results;
        }

        if (discoveredConfigPaths != null)
        {
            foreach (var path in configPaths)
                discoveredConfigPaths.Add(path);
        }

        try
        {
            foreach (var configPath in configPaths)
            {
                if (!File.Exists(configPath))
                    continue;

                var json = await File.ReadAllTextAsync(configPath);
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StoreImport] Heroic Import Error: {ex.Message}");
        }

        return results;
    }

    private List<string> GetHeroicGogConfigPaths(string? manualConfigPath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manualResolved = ResolveHeroicGogConfigPath(manualConfigPath);
        if (!string.IsNullOrWhiteSpace(manualResolved) && File.Exists(manualResolved))
            paths.Add(Path.GetFullPath(manualResolved));

        if (_settings.HeroicGogConfigPaths is { Count: > 0 })
        {
            foreach (var storedPath in _settings.HeroicGogConfigPaths)
            {
                var resolved = ResolveHeroicGogConfigPath(storedPath);
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                    paths.Add(Path.GetFullPath(resolved));
            }
        }

        foreach (var candidate in GetHeroicGogCandidatePaths())
        {
            if (!File.Exists(candidate))
                continue;

            paths.Add(Path.GetFullPath(candidate));
        }

        return paths.ToList();
    }

    private IEnumerable<string> GetHeroicGogCandidatePaths()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfig) && Path.IsPathRooted(xdgConfig))
            yield return Path.Combine(xdgConfig, "heroic", "gog_store", "installed.json");

        foreach (var home in GetHomeCandidates())
            yield return Path.Combine(home, _heroicGogConfigSuffix);
    }

    private static string? ResolveHeroicGogConfigPath(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(inputPath);
        }
        catch
        {
            return null;
        }

        if (File.Exists(fullPath))
            return fullPath;

        if (!Directory.Exists(fullPath))
            return null;

        var fileName = Path.GetFileName(fullPath);

        if (string.Equals(fileName, "gog_store", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(fullPath, "installed.json");
            if (File.Exists(candidate))
                return candidate;
        }

        if (string.Equals(fileName, "heroic", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(fullPath, "gog_store", "installed.json");
            if (File.Exists(candidate))
                return candidate;
        }

        var gogStoreCandidate = Path.Combine(fullPath, "gog_store", "installed.json");
        if (File.Exists(gogStoreCandidate))
            return gogStoreCandidate;

        var installedCandidate = Path.Combine(fullPath, "installed.json");
        if (File.Exists(installedCandidate))
            return installedCandidate;

        return null;
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
            // Command to launch via Heroic CLI (Heroic will handle the URL argument).
            Files = new List<MediaFileRef>
            {
                new()
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "heroic",
                    Index = 1,
                    Label = "Heroic"
                }
            },
            LauncherArgs = $"gog://{id}",
            MediaType = MediaType.Command,
            Description = $"Imported from Heroic (GOG) - Platform: {platform}",
            Developer = "GOG"
        };
    }
}
