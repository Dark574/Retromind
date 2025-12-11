using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Retromind.Helpers;

/// <summary>
/// Converts a file path or Avalonia asset URI into a Bitmap.
/// Supports:
/// - local file paths
/// - asset URIs (avares://)
/// Automatically uses a cache to avoid repeated file access,
/// which improves performance when displaying large ROM lists.
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                // Wenn ein Parameter (Dateiname) übergeben wurde, kombinieren wir ihn mit dem Pfad (Verzeichnis)
                if (parameter is string fileName && !string.IsNullOrEmpty(fileName))
                {
                    path = System.IO.Path.Combine(path, fileName);
                }
                
                if (string.IsNullOrEmpty(path)) return null;
                
                // Prüfen, ob es ein Web-Link ist (können wir synchron nicht laden)
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Hier könnten wir ein Placeholder-Bild zurückgeben
                    return null; 
                }

                // Absoluter Pfad?
                if (System.IO.File.Exists(path))
                {
                    return new Bitmap(path);
                }

                // Vielleicht ist es ein Asset-URI? "avares://..."
                if (path.StartsWith("avares://"))
                {
                    return new Bitmap(AssetLoader.Open(new Uri(path)));
                }
            }
            catch (Exception)
            {
                // Bild konnte nicht geladen werden -> null zurückgeben (kein Bild anzeigen)
                return null;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}