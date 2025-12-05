using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Retromind.Helpers;

/// <summary>
///     Nimmt einen String (Dateipfad) und versucht, daraus ein Bitmap zu laden.
///     Gibt null zurück, wenn der Pfad ungültig ist oder das Bild nicht existiert.
/// </summary>
public class BitmapAssetValueConverter : IValueConverter
{
    public static BitmapAssetValueConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
            try
            {
                // Wenn der Pfad nicht absolut ist (z.B. "Medien/Sega/Sonic.jpg"),
                // dann basteln wir den Pfad zur Executable davor.
                if (!Path.IsPathRooted(path))
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    path = Path.Combine(baseDir, path);
                }

                if (File.Exists(path)) return new Bitmap(path);
            }
            catch (Exception)
            {
                // Fehler beim Laden ignorieren (wir geben null zurück, UI zeigt Fallback)
            }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}