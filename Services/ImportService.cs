using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

public class ImportService
{
    /// <summary>
    ///     Durchsucht einen Ordner rekursiv nach Dateien mit bestimmten Endungen
    ///     und gibt eine Liste von MediaItems zurück.
    /// </summary>
    public async Task<List<MediaItem>> ImportFromFolderAsync(string sourceFolder, string[] extensions)
    {
        return await Task.Run(() =>
        {
            var results = new List<MediaItem>();

            // Extensions normalisieren (müssen mit . beginnen für Path.GetExtension)
            var validExtensions = extensions
                .Select(e => e.Trim().ToLower())
                .Select(e => e.StartsWith(".") ? e : "." + e)
                .ToHashSet();

            try
            {
                var files = Directory.EnumerateFiles(sourceFolder, "*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLower();
                    if (validExtensions.Contains(ext))
                    {
                        var title = Path.GetFileNameWithoutExtension(file);

                        // Optional: Bereinigen des Titels (z.B. "(USA)", "[!]" entfernen)
                        // title = CleanTitle(title);

                        var item = new MediaItem
                        {
                            Title = title,
                            FilePath = file,
                            MediaType = MediaType.Native // Default, wird im VM ggf. angepasst
                        };

                        results.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Importieren: {ex.Message}");
            }

            return results;
        });
    }
}