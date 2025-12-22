using System;
using System.Collections.Generic;
using System.IO;
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

    public static Theme LoadTheme(string filePath)
    {
        // Allow passing "Wheel/theme.axaml" etc. (relative to portable ThemesRoot).
        if (!string.IsNullOrWhiteSpace(filePath) && !Path.IsPathRooted(filePath))
            filePath = Path.Combine(AppPaths.ThemesRoot, filePath);

        filePath = AppPaths.ResolveDataPath(filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            var errorView = CreateErrorView(Strings.Theme_Error_InvalidPath);
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, videoEnabled: false);
        }

        var themeDir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(themeDir) || !File.Exists(filePath))
        {
            var errorView = CreateErrorView(string.Format(Strings.Theme_Error_FileNotFoundFormat, filePath));
            
            // In error mode we reset ThemeBasePath to avoid pointing to a stale directory.
            ThemeProperties.ThemeBasePath = null;
            
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, videoEnabled: false);
        }

        try
        {
            // At this point we have a valid theme file.
            // Make the directory available globally for theme-local assets (cabinet, pointer, sounds, ...).
            ThemeProperties.ThemeBasePath = themeDir;
            
            var xamlContent = ReadXamlWithCache(filePath);

            var view = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlContent)
                       ?? CreateErrorView(Strings.Theme_Error_LoadedNullOrInvalid);

            var sounds = new ThemeSounds
            {
                Navigate = ThemeProperties.GetNavigateSound(view),
                Confirm = ThemeProperties.GetConfirmSound(view),
                Cancel = ThemeProperties.GetCancelSound(view)
            };

            var videoEnabled = ThemeProperties.GetVideoEnabled(view);

            var videoSlotName = ThemeProperties.GetVideoSlotName(view);

            var themeName = ThemeProperties.GetName(view);
            var themeAuthor = ThemeProperties.GetAuthor(view);
            var themeVersion = ThemeProperties.GetVersion(view);
            var themeWebsiteUrl = ThemeProperties.GetWebsiteUrl(view);

            return new Theme(
                view,
                sounds,
                themeDir,
                videoEnabled: videoEnabled,
                videoSlotName: videoSlotName,
                name: themeName,
                author: themeAuthor,
                version: themeVersion,
                websiteUrl: themeWebsiteUrl);
        }
        catch (Exception ex)
        {
            var errorView = CreateErrorView(string.Format(Strings.Theme_Error_LoadFailedFormat, ex.Message));
            return new Theme(errorView, new ThemeSounds(), AppPaths.DataRoot, videoEnabled: false);
        }
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