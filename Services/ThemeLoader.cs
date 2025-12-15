using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Avalonia;
using Retromind.Extensions;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Services;

/// <summary>
/// Service responsible for loading external .axaml theme files at runtime.
/// This allows users to create custom skins without recompiling the application.
/// </summary>
public static class ThemeLoader
{
    /// <summary>
    /// Loads an .axaml file from disk and parses it into an Avalonia Control.
    /// </summary>
    /// <param name="filePath">Absolute path to the .axaml file.</param>
    /// <returns>The loaded Control or an error message view.</returns>
    public static Theme LoadTheme(string filePath)
    {
        var themeDir = Path.GetDirectoryName(filePath);

        if (string.IsNullOrEmpty(themeDir) || !File.Exists(filePath))
        {
            var errorView = CreateErrorView($"Theme file not found: {filePath}");
            return new Theme(errorView, new ThemeSounds(), AppDomain.CurrentDomain.BaseDirectory);
        }

        try
        {
            string xamlContent = File.ReadAllText(filePath);
            var view = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlContent) 
                       ?? CreateErrorView("Loaded theme was null or invalid.");

            var sounds = new ThemeSounds
            {
                Navigate = ThemeProperties.GetNavigateSound(view),
                Confirm = ThemeProperties.GetConfirmSound(view),
                Cancel = ThemeProperties.GetCancelSound(view)
            };
            
            return new Theme(view, sounds, themeDir);
        }
        catch (Exception ex)
        {
            var errorView = CreateErrorView($"Error loading theme:\n{ex.Message}");
            return new Theme(errorView, new ThemeSounds(), AppDomain.CurrentDomain.BaseDirectory);
        }
    }

    private static Control CreateErrorView(string message)
    {
        var stackPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 20
        };

        stackPanel.Children.Add(new TextBlock 
        { 
            Text = message, 
            FontSize = 20, 
            Foreground = Brushes.Red,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 800
        });

        var btn = new Button
        {
            Content = "Close / Schließen",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 20,
            Padding = new Avalonia.Thickness(20, 10)
        };
        
        // Wir binden den Button an das 'ExitBigModeCommand' vom ViewModel
        btn.Bind(Button.CommandProperty, new Binding("ForceExitCommand"));

        stackPanel.Children.Add(btn);

        return stackPanel;
    }
}