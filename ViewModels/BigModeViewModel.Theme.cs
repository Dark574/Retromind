using System;
using System.IO;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    /// <summary>
    /// Chooses and applies a theme for the given node.
    /// Resolution order:
    /// 1) Default theme: "<AppBase>/Themes/Default.axaml"
    /// 2) Node.ThemePath:
    ///    - absolute path (if it exists)
    ///    - relative to "<AppBase>/Themes" (if it exists)
    /// 3) Convention: "<AppBase>/Themes/<SafeNodeName>/theme.axaml"
    ///
    /// If the resulting theme differs from the currently active theme, RequestThemeChange is raised.
    /// </summary>
    private void UpdateThemeForNode(MediaNode? node)
    {
        if (node == null) return;

        // Expose the current node to the theme (e.g. for background/logo bindings).
        CurrentNode = node;

        var themesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        var defaultThemePath = Path.Combine(themesRoot, "Default.axaml");

        var themeToLoad = defaultThemePath;

        if (!string.IsNullOrEmpty(node.ThemePath))
        {
            // Prefer an absolute path if the user provided one.
            if (File.Exists(node.ThemePath))
            {
                themeToLoad = node.ThemePath;
            }
            else
            {
                // Otherwise treat it as relative to the Themes root.
                var candidate = Path.Combine(themesRoot, node.ThemePath);
                if (File.Exists(candidate))
                    themeToLoad = candidate;
            }
        }
        else
        {
            // Convention: Themes/<SafeNodeName>/theme.axaml
            var safeName = string.Join("_", node.Name.Split(Path.GetInvalidFileNameChars()));
            var conventionPath = Path.Combine(themesRoot, safeName, "theme.axaml");
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