using System;
using System.IO;
using System.Text.Json;

namespace Retromind.Helpers;

public static class PortableEnvironment
{
    private readonly record struct PortableHomeMode(bool Enabled, bool Force);

    public static void ApplyPortableXdgPaths()
    {
        // AppImage-specific behavior: only apply if running from an AppImage.
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appImage) && string.IsNullOrWhiteSpace(appDir))
            return;

        var mode = ReadPortableHomeMode();
        if (!mode.Enabled)
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

        if (mode.Force)
        {
            SetAlways("HOME", homeRoot);
            SetAlways("XDG_CONFIG_HOME", xdgConfig);
            SetAlways("XDG_DATA_HOME", xdgData);
            SetAlways("XDG_CACHE_HOME", xdgCache);
            SetAlways("XDG_STATE_HOME", xdgState);
            SetAlways("DOTNET_CLI_HOME", dotnetHome);
        }
        else
        {
            SetIfMissing("HOME", homeRoot);
            SetIfMissing("XDG_CONFIG_HOME", xdgConfig);
            SetIfMissing("XDG_DATA_HOME", xdgData);
            SetIfMissing("XDG_CACHE_HOME", xdgCache);
            SetIfMissing("XDG_STATE_HOME", xdgState);
            SetIfMissing("DOTNET_CLI_HOME", dotnetHome);
        }
    }

    private static PortableHomeMode ReadPortableHomeMode()
    {
        var settingsPath = Path.Combine(AppPaths.DataRoot, "app_settings.json");
        if (!File.Exists(settingsPath))
            return default;

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return default;

            if (!doc.RootElement.TryGetProperty("UsePortableHomeInAppImage", out var prop))
                return default;

            if (prop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return default;

            var enabled = prop.GetBoolean();
            if (!enabled)
                return default;

            var force = false;
            if (doc.RootElement.TryGetProperty("ForcePortableHomeInAppImage", out var forceProp) &&
                forceProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                force = forceProp.GetBoolean();
            }

            return new PortableHomeMode(Enabled: true, Force: force);
        }
        catch
        {
            return default;
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

    private static void SetAlways(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
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
