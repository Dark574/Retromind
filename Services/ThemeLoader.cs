using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Services;

/// <summary>
/// Loads external .axaml theme files at runtime.
/// This enables user-created skins without recompiling the application.
/// </summary>
public static class ThemeLoader
{
    private sealed record CachedXaml(string Content, DateTime LastWriteTimeUtc);

    private static readonly Dictionary<string, CachedXaml> XamlCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Theme LoadTheme(string filePath, bool setGlobalBasePath = true)
    {
        // Allow passing "Wheel/theme.axaml" etc. (relative to portable ThemesRoot).
        if (!string.IsNullOrWhiteSpace(filePath) && !Path.IsPathRooted(filePath))
            filePath = Path.Combine(AppPaths.ThemesRoot, filePath);

        filePath = AppPaths.ResolveDataPath(filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            var errorView = CreateErrorView(Strings.Theme_Error_InvalidPath);
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, primaryVideoEnabled: false, secondaryVideoEnabled: false);
        }

        var themeDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(themeDir) || !File.Exists(filePath))
        {
            var errorView = CreateErrorView(string.Format(Strings.Theme_Error_FileNotFoundFormat, filePath));
            
            // In error mode we reset the legacy global path to avoid pointing to a stale directory.
            if (setGlobalBasePath)
                ThemeProperties.GlobalThemeBasePath = null;
            
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, primaryVideoEnabled: false, secondaryVideoEnabled: false);
        }

        try
        {
            // At this point we have a valid theme file.
            // Store the base path on the view instance so multiple themes can coexist.
            
            var xamlContent = ReadXamlWithCache(filePath);

            // Parse the initial view instance for host usage.
            var view = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlContent)
                       ?? CreateErrorView(Strings.Theme_Error_LoadedNullOrInvalid);
            ThemeProperties.SetThemeBasePath(view, themeDir);
            NormalizeThemeTypography(view, themeDir);

            if (setGlobalBasePath)
                ThemeProperties.GlobalThemeBasePath = themeDir;

            var sounds = new ThemeSounds
            {
                Navigate = ThemeProperties.GetNavigateSound(view),
                Confirm = ThemeProperties.GetConfirmSound(view),
                Cancel = ThemeProperties.GetCancelSound(view)
            };
            
            var secondaryBackgroundVideoPath = ThemeProperties.GetSecondaryBackgroundVideoPath(view);
            
            var primaryEnabledRaw = ThemeProperties.GetPrimaryVideoEnabled(view);
            var secondaryEnabledRaw = ThemeProperties.GetSecondaryVideoEnabled(view);

            // Default behaviour if not specified:
            //  - Primary: enabled
            //  - Secondary: enabled when a background path is set, otherwise disabled
            var primaryEnabled = primaryEnabledRaw ?? true;
            var secondaryEnabled = secondaryEnabledRaw
                                   ?? (!string.IsNullOrWhiteSpace(secondaryBackgroundVideoPath));

            var videoSlotName = ThemeProperties.GetVideoSlotName(view);

            var themeName = ThemeProperties.GetName(view);
            var themeAuthor = ThemeProperties.GetAuthor(view);
            var themeVersion = ThemeProperties.GetVersion(view);
            var themeWebsiteUrl = ThemeProperties.GetWebsiteUrl(view);

            var attractEnabled = ThemeProperties.GetAttractModeEnabled(view);
            var attractIdleSeconds = ThemeProperties.GetAttractModeIdleSeconds(view);
            var attractSound = ThemeProperties.GetAttractModeSound(view);

            TimeSpan? attractInterval = null;
            if (attractEnabled && attractIdleSeconds > 0)
            {
                attractInterval = TimeSpan.FromSeconds(attractIdleSeconds);
            }

            // Factory that creates fresh view instances from the cached XAML string.
            // This is used for scenarios where the same theme needs to be instantiated
            // multiple times (e.g. system subthemes in BigMode).
            Control ViewFactory()
            {
                var fresh = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlContent)
                            ?? CreateErrorView(Strings.Theme_Error_LoadedNullOrInvalid);
                ThemeProperties.SetThemeBasePath(fresh, themeDir);
                NormalizeThemeTypography(fresh, themeDir);
                return fresh;
            }
            
            return new Theme(
                view,
                sounds,
                themeDir,
                secondaryBackgroundVideoPath: secondaryBackgroundVideoPath,
                primaryVideoEnabled: primaryEnabled,
                secondaryVideoEnabled: secondaryEnabled,
                videoSlotName: videoSlotName,
                name: themeName,
                author: themeAuthor,
                version: themeVersion,
                websiteUrl: themeWebsiteUrl,
                attractModeEnabled: attractEnabled,
                attractModeIdleInterval: attractInterval,
                attractModeSoundPath: attractSound,
                viewFactory: ViewFactory);
        }
        catch (Exception ex)
        {
            var errorView = CreateErrorView(string.Format(Strings.Theme_Error_LoadFailedFormat, ex.Message));
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, primaryVideoEnabled: false,
                secondaryVideoEnabled: false);
        }
    }

    private static void NormalizeThemeTypography(Control view, string themeDir)
    {
        NormalizeFontSpec(view, ThemeProperties.GetTitleFontFamily(view), ThemeProperties.SetTitleFontFamily, themeDir);
        NormalizeFontSpec(view, ThemeProperties.GetBodyFontFamily(view), ThemeProperties.SetBodyFontFamily, themeDir);
        NormalizeFontSpec(view, ThemeProperties.GetCaptionFontFamily(view), ThemeProperties.SetCaptionFontFamily, themeDir);
        NormalizeFontSpec(view, ThemeProperties.GetMonoFontFamily(view), ThemeProperties.SetMonoFontFamily, themeDir);
    }

    private static void NormalizeFontSpec(
        Control view,
        string? spec,
        Action<AvaloniaObject, string?> setter,
        string themeDir)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return;

        var hashIndex = spec.IndexOf('#', StringComparison.Ordinal);
        var pathPart = hashIndex >= 0 ? spec[..hashIndex] : spec;
        var familyPart = hashIndex >= 0 ? spec[hashIndex..] : string.Empty;

        if (!LooksLikePath(pathPart) || Path.IsPathRooted(pathPart))
            return;

        var resolved = Path.Combine(themeDir, pathPart) + familyPart;
        setter(view, resolved);
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal))
            return true;

        var ext = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(ext))
            return false;

        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadXamlWithCache(string filePath)
    {
        var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);

        if (XamlCache.TryGetValue(filePath, out var cached) &&
            cached.LastWriteTimeUtc == lastWriteUtc)
        {
            return cached.Content;
        }

        var content = File.ReadAllText(filePath);
        XamlCache[filePath] = new CachedXaml(content, lastWriteUtc);
        return content;
    }

    private static Control CreateErrorView(string message)
    {
        var stackPanel = new StackPanel
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 20
        };

        stackPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 20,
            Foreground = Brushes.Red,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            MaxWidth = 800
        });

        var closeButton = new Button
        {
            Content = Strings.Button_Close,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            FontSize = 20,
            Padding = new Avalonia.Thickness(20, 10)
        };

        // This is used in BigMode: the host VM provides ForceExitCommand.
        closeButton.Bind(Button.CommandProperty, new Binding("ForceExitCommand"));

        stackPanel.Children.Add(closeButton);

        return stackPanel;
    }
}
