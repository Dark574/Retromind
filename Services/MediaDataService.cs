using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Resources;

// Namespace für Strings

namespace Retromind.Services;

public class MediaDataService
{
    private const string FileName = "retromind_tree.json";
    private const string BackupFileName = "retromind_tree.bak";

    // Pfad zur Datei im gleichen Ordner wie die Executable
    private string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
    private string BackupPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BackupFileName);

    public async Task SaveAsync(ObservableCollection<MediaNode> nodes)
    {
        try
        {
            // 1. Erstmal ein Backup der existierenden Datei machen, falls vorhanden
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);

            var options = new JsonSerializerOptions { WriteIndented = true };

            // 2. Direkt speichern (man könnte auch erst in .tmp speichern und moven, aber Backup reicht für den Anfang)
            using var stream = File.Create(FilePath);
            await JsonSerializer.SerializeAsync(stream, nodes, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Speichern: {ex.Message}");

            // Wenn Speichern fehlschlägt, versuchen wir das Backup wiederherzustellen, 
            // damit wir nicht mit einer halbgeschriebenen (leeren) Datei enden.
            try
            {
                if (File.Exists(BackupPath)) File.Copy(BackupPath, FilePath, true);
            }
            catch
            {
                /* Worst Case */
            }
        }
    }

    public async Task<ObservableCollection<MediaNode>> LoadAsync()
    {
        // Variable HIER deklarieren, damit sie überall sichtbar ist
        ObservableCollection<MediaNode>? result = null;

        // Versuche Hauptdatei zu laden
        try
        {
            if (File.Exists(FilePath))
            {
                using var stream = File.OpenRead(FilePath);
                result = await JsonSerializer.DeserializeAsync<ObservableCollection<MediaNode>>(stream);
                if (result != null && result.Count > 0) return result;
            }
        }
        catch (JsonException ex)
        {
            // WICHTIG: Hier nicht einfach weitermachen!
            // In einer GUI App ist Console.WriteLine schlecht sichtbar.
            // Wir werfen den Fehler weiter oder loggen ihn so, dass die App NICHT speichert.
            Console.WriteLine($"KRITISCH: Hauptdatei korrupt: {ex.Message}");

            // Strategie: Wir benennen die defekte Datei um, damit sie nicht überschrieben wird!
            var corruptPath = FilePath + ".corrupt-" + DateTime.Now.Ticks;
            File.Move(FilePath, corruptPath);

            // Jetzt können wir versuchen, das Backup zu laden
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Allgemeiner Fehler: {ex.Message}");
        }

        // Wenn Hauptdatei fehlschlägt (oder nicht da war), versuche Backup
        Console.WriteLine("Versuche Backup...");
        result = await LoadFromFileAsync(BackupPath);
        if (result != null && result.Count > 0) return result;

        // Wenn gar nichts geht -> Neue leere Struktur
        return new ObservableCollection<MediaNode>
        {
            // Verwende jetzt den lokalisierten String aus den Ressourcen
            new(Strings.Library, NodeType.Area)
        };
    }

    private async Task<ObservableCollection<MediaNode>?> LoadFromFileAsync(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            var result = await JsonSerializer.DeserializeAsync<ObservableCollection<MediaNode>>(stream);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Laden von {path}: {ex.Message}");
            return null; // Signalisieren, dass es nicht geklappt hat
        }
    }
}