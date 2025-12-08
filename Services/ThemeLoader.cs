using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Data;

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
    public static Control LoadTheme(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return CreateErrorView($"Theme file not found: {filePath}");
        }

        try
        {
            // Read the XAML content from the file
            string xamlContent = File.ReadAllText(filePath);
            
            // Parse the string into an actual Avalonia object
            // This requires the external XAML to be valid
            var loadedObj = AvaloniaRuntimeXamlLoader.Parse<Control>(xamlContent);
            
            return loadedObj ?? CreateErrorView("Loaded theme was null.");
        }
        catch (Exception ex)
        {
            // Fallback view in case of syntax errors in the user theme
            return CreateErrorView($"Error loading theme:\n{ex.Message}");
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
        btn.Bind(Button.CommandProperty, new Binding("ExitBigModeCommand"));

        stackPanel.Children.Add(btn);

        return stackPanel;
    }
}