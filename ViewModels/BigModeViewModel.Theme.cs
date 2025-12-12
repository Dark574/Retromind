using System;
using System.IO;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private void UpdateThemeForNode(MediaNode? node)
    {
        if (node == null) return;

        CurrentNode = node;

        var themesBaseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        var themeToLoad = Path.Combine(themesBaseDir, "Default.axaml");

        if (!string.IsNullOrEmpty(node.ThemePath))
        {
            if (File.Exists(node.ThemePath))
            {
                themeToLoad = node.ThemePath;
            }
            else
            {
                var candidate = Path.Combine(themesBaseDir, node.ThemePath);
                if (File.Exists(candidate))
                    themeToLoad = candidate;
            }
        }
        else
        {
            var safeName = string.Join("_", node.Name.Split(Path.GetInvalidFileNameChars()));
            var conventionPath = Path.Combine(themesBaseDir, safeName, "theme.axaml");
            if (File.Exists(conventionPath))
                themeToLoad = conventionPath;
        }

        if (!string.Equals(_currentThemePath, themeToLoad, StringComparison.OrdinalIgnoreCase))
        {
            _currentThemePath = themeToLoad;
            CurrentThemeDirectory = Path.GetDirectoryName(themeToLoad) ?? string.Empty;
            RequestThemeChange?.Invoke(themeToLoad);
        }
    }
}