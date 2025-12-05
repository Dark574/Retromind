using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Retromind.Helpers;

public class AsyncImageHelper : AvaloniaObject
{
    // Cache, damit wir das gleiche Bild nicht 10x laden
    private static readonly Dictionary<string, Bitmap> Cache = new();
    private static readonly HttpClient HttpClient = new();

    // Attached Property: Url
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("Url");

    public static string? GetUrl(Image element)
    {
        return element.GetValue(UrlProperty);
    }

    public static void SetUrl(Image element, string? value)
    {
        element.SetValue(UrlProperty, value);
    }

    // Methode um Bild aus Cache zu speichern
    public static async Task<bool> SaveCachedImageAsync(string url, string destinationPath)
    {
        if (Cache.TryGetValue(url, out var bitmap))
        {
            return await Task.Run(() =>
            {
                try
                {
                    bitmap.Save(destinationPath); // Avalonia Bitmap Save
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fehler beim Speichern aus Cache: {ex.Message}");
                    return false;
                }
            });
        }
        return false; // Nicht im Cache
    }
    
    static AsyncImageHelper()
    {
        // WICHTIG: User-Agent setzen!
        // Viele Server (ComicVine, Wikipedia, etc.) blockieren requests ohne Agent.
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "Retromind-MediaManager/1.0");

        // Listener: Wenn sich Url ändert, lade Bild
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            var url = args.NewValue as string;
            LoadImageAsync(image, url);
        });
    }

    private static async void LoadImageAsync(Image image, string? url)
    {
        // Bild erstmal leeren (oder Placeholder setzen)
        image.Source = null;

        if (string.IsNullOrEmpty(url)) return;

        // 1. Cache Check
        if (Cache.TryGetValue(url, out var cachedBitmap))
        {
            image.Source = cachedBitmap;
            return;
        }

        try
        {
            // Frischer Client für jeden Request
            using var client = new HttpClient();
            // Headers setzen, um 403 zu vermeiden
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // 2. Download
            var data = await HttpClient.GetByteArrayAsync(url);

            // 3. Erstellen (muss im UI Thread sicher sein, aber Bitmap Konstruktor ist meist ok)
            // Wir packen es trotzdem in Dispatcher, sicher ist sicher.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var stream = new MemoryStream(data); 
                    stream.Position = 0;
                    var bitmap = new Bitmap(stream);
                    
                    // In Cache packen
                    Cache[url] = bitmap;
                    
                    // Nur setzen, wenn URL noch die gleiche ist (falls User schnell scrollt)
                    if (GetUrl(image) == url)
                    {
                        image.Source = bitmap;
                        Console.WriteLine($"Source gesetzt für {url}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bild-Fehler: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download-Fehler für {url}: {ex.Message}");
        }
    }
}