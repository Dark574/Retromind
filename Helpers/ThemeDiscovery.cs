using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Services;

namespace Retromind.Helpers;

public static class ThemeDiscovery
{
    public static IReadOnlyList<string> GetAvailableThemePaths()
    {
        try
        {
            if (!Directory.Exists(AppPaths.ThemesRoot))
                return [];

            return Directory.EnumerateDirectories(AppPaths.ThemesRoot)
                .Select(directory => new
                {
                    Name = Path.GetFileName(directory),
                    ThemeFile = Path.Combine(directory, "theme.axaml")
                })
                .Where(theme => File.Exists(theme.ThemeFile))
                .Select(theme => $"{theme.Name}/theme.axaml")
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            // Best effort: unavailable theme folders must not prevent opening an editor.
            return [];
        }
    }
}
