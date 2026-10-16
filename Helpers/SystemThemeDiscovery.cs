using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Retromind.Services;

namespace Retromind.Helpers;

public sealed class SystemThemeOption
{
    public required string Id { get; init; }          // "Default", "C64", ...
    public required string DisplayName { get; init; } // Display name in the dropdown
}

public static class SystemThemeDiscovery
{
    public const string DefaultThemeId = "Default";

    public static List<SystemThemeOption> GetAvailableSystemThemes()
    {
        var result = new List<SystemThemeOption>();

        // Base directory: .../Themes/System
        var systemRoot = Path.Combine(AppPaths.ThemesRoot, "System");
        if (!Directory.Exists(systemRoot))
            return result;

        // Scan all subfolders that contain a theme.axaml
        foreach (var dir in Directory.GetDirectories(systemRoot))
        {
            var id = Path.GetFileName(dir);
            var relativePath = Path.Combine("System", id, "theme.axaml");
            if (!ThemePathResolver.TryResolveThemeFilePath(
                    AppPaths.ThemesRoot,
                    relativePath,
                    out _))
                continue;

            var displayName = id;

            try
            {
                // Use the existing ThemeLoader so we get ThemeProperties.Name, VideoEnabled, etc.
                // Passing a relative path keeps it portable (ThemeLoader resolves via AppPaths.ThemesRoot).
                var theme = ThemeLoader.LoadTheme(relativePath);

                if (!string.IsNullOrWhiteSpace(theme.Name))
                    displayName = theme.Name!;
            }
            catch
            {
                // Best-effort only: if loading fails, fall back to the folder name.
            }
            result.Add(new SystemThemeOption
            {
                Id = id,
                DisplayName = displayName
            });
        }

        // Sort alphabetically by display name
        result = result
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Ensure there is always a "Default" entry at the top
        if (result.All(r => !string.Equals(r.Id, DefaultThemeId, StringComparison.OrdinalIgnoreCase)))
        {
            result.Insert(0, new SystemThemeOption
            {
                Id = DefaultThemeId,
                DisplayName = "(Standard)"
            });
        }

        return result;
    }
}
