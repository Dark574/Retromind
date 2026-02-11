using System;
using System.IO;
using System.Text.Json;

namespace Retromind.Helpers;

public static class PortableEnvironment
{
    public static void ApplyPortableXdgPaths()
    {
        // AppImage-specific behavior: only apply if running from an AppImage.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appImage) && string.IsNullOrWhiteSpace(appDir))
            return;

        if (!ShouldUsePortableHome())
            return;

        var dataRoot = AppPaths.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot))
            return;

        var homeRoot = Path.Combine(dataRoot, "Home");
        var xdgConfig = Path.Combine(homeRoot, ".config");
        var xdgData = Path.Combine(homeRoot, ".local", "share");
        var xdgCache = Path.Combine(homeRoot, ".cache");
        var xdgState = Path.Combine(homeRoot, ".local", "state");
        var dotnetHome = Path.Combine(homeRoot, ".dotnet");

        CreateDirectorySafe(xdgConfig);
        CreateDirectorySafe(xdgData);
        CreateDirectorySafe(xdgCache);
        CreateDirectorySafe(xdgState);
        CreateDirectorySafe(dotnetHome);

        SetIfMissing("HOME", homeRoot);
        SetIfMissing("XDG_CONFIG_HOME", xdgConfig);
        SetIfMissing("XDG_DATA_HOME", xdgData);
        SetIfMissing("XDG_CACHE_HOME", xdgCache);
        SetIfMissing("XDG_STATE_HOME", xdgState);
        SetIfMissing("DOTNET_CLI_HOME", dotnetHome);
    }

    private static bool ShouldUsePortableHome()
    {
        var settingsPath = Path.Combine(AppPaths.DataRoot, "app_settings.json");
        if (!File.Exists(settingsPath))
            return false;

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("UsePortableHomeInAppImage", out var prop))
                return false;

            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return prop.GetBoolean();

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void SetIfMissing(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return;

        var existing = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(existing))
            return;

        Environment.SetEnvironmentVariable(key, value);
    }

    private static void CreateDirectorySafe(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                Directory.CreateDirectory(path);
        }
        catch
        {
            // Best-effort: never block startup if the path can't be created.
        }
    }
}
