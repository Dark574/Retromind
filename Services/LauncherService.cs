using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Retromind.Models;

// List<string>

public class LauncherService
{
    // nodePath Parameter (optional, falls wir ihn nicht haben, nutzen wir Fallback)
    public async Task LaunchAsync(MediaItem item, EmulatorConfig? inheritedConfig = null, List<string>? nodePath = null)
    {
        if (item == null) return;

        string fileName;
        string? templateArgs = null;

        // 1. Welchen Launcher/Emulator nutzen wir?
        if (!string.IsNullOrEmpty(item.LauncherPath))
        {
            // Individuelle Config
            fileName = item.LauncherPath;
            templateArgs = string.IsNullOrWhiteSpace(item.LauncherArgs) ? "{file}" : item.LauncherArgs;
        }
        else if (inheritedConfig != null)
        {
            // Vererbter Emulator
            fileName = inheritedConfig.Path;
            templateArgs = inheritedConfig.Arguments;
        }
        else
        {
            // Native (Direktstart)
            if (string.IsNullOrEmpty(item.FilePath)) return;
            fileName = item.FilePath;
            templateArgs = item.LauncherArgs; // Kann null sein
        }

        try
        {
            var isNative = string.IsNullOrEmpty(item.LauncherPath) && inheritedConfig == null;

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                // UseShellExecute = false erlaubt uns ArgumentList und EnvironmentVariables zu nutzen
                UseShellExecute = isNative
            };

            // Working Directory setzen
            if (File.Exists(fileName))
                startInfo.WorkingDirectory = Path.GetDirectoryName(fileName) ?? string.Empty;
            else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                startInfo.WorkingDirectory = Path.GetDirectoryName(item.FilePath) ?? string.Empty;

            // --- PORTABLE WINE PREFIX LOGIK ---
            if (!isNative)
            {
                string? prefixPath = null;
                string? relativePrefixPathToSave = null;

                // Fall 1: Wir haben schon einen gespeicherten Pfad im Item?
                if (!string.IsNullOrEmpty(item.PrefixPath))
                {
                    // Pfad ist relativ gespeichert (z.B. "Games/PC/Prefixes/Super_Mario")
                    // Wir müssen ihn absolut machen
                    prefixPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Medien", item.PrefixPath);
                }
                // Fall 2: Noch kein Pfad, aber wir haben NodePath -> Neu anlegen
                else if (nodePath != null)
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;

                    // Wir bauen den relativen Pfad für die Speicherung
                    var relPaths = new List<string>();
                    relPaths.AddRange(nodePath);
                    relPaths.Add("Prefixes");

                    // Ordnername basierend auf Titel (einmalig generiert!)
                    var safeTitle = string.Join("_", item.Title.Split(Path.GetInvalidFileNameChars()))
                        .Replace(" ", "_");
                    relPaths.Add(safeTitle);

                    relativePrefixPathToSave = Path.Combine(relPaths.ToArray()); // z.B. "Games/PC/Prefixes/Super_Mario"

                    // Absoluter Pfad für Environment
                    prefixPath = Path.Combine(baseDir, "Medien", relativePrefixPathToSave);
                }
                // Fall 3: Fallback (neben ROM), wenn alles fehlt
                else if (!string.IsNullOrEmpty(item.FilePath))
                {
                    var gameDir = Path.GetDirectoryName(item.FilePath);
                    if (!string.IsNullOrEmpty(gameDir)) prefixPath = Path.Combine(gameDir, "pfx");
                }

                if (!string.IsNullOrEmpty(prefixPath))
                {
                    if (!Directory.Exists(prefixPath)) Directory.CreateDirectory(prefixPath);
                    startInfo.EnvironmentVariables["WINEPREFIX"] = prefixPath;

                    // WICHTIG: Wenn wir einen neuen Pfad generiert haben (und er im Medien-Ordner liegt),
                    // speichern wir ihn jetzt im Item, damit er fixiert bleibt!
                    if (relativePrefixPathToSave != null) item.PrefixPath = relativePrefixPathToSave;
                }
            }
            // ----------------------------------

            // --- ARGUMENTE BAUEN ---

            if (isNative)
            {
                // Bei Native (UseShellExecute=true) müssen wir den String nehmen
                startInfo.Arguments = templateArgs ?? "";
            }
            else
            {
                // Intelligente Argument-Übergabe für Launcher

                // Vorbereitung: Datei-Pfad
                var fullPath = string.IsNullOrEmpty(item.FilePath) ? "" : Path.GetFullPath(item.FilePath);

                // Fall A: Einfaches Template "{file}" (Standard bei UMU, Emulatoren)
                if (string.IsNullOrWhiteSpace(templateArgs) || templateArgs.Trim() == "{file}")
                {
                    if (!string.IsNullOrEmpty(fullPath)) startInfo.ArgumentList.Add(fullPath);
                }
                // Fall B: Komplexes Template (z.B. "--fullscreen {file} --no-gui")
                else
                {
                    // VERSUCH: Template splitten und ArgumentList füllen
                    // Wir gehen davon aus, dass Argumente im Template durch Leerzeichen getrennt sind.
                    // Das ist robust für 99% der Fälle.
                    // Wenn ein Argument selbst Leerzeichen enthält (außer {file}), müssten wir einen echten Parser schreiben.

                    var parts = templateArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                        if (part.Contains("{file}"))
                        {
                            // Wenn {file} Teil eines Arguments ist (z.B. --rom={file})
                            var resolved = part.Replace("{file}", fullPath); // KEINE Quotes hinzufügen!
                            startInfo.ArgumentList.Add(resolved);
                        }
                        else
                        {
                            // Normales Argument (z.B. --fullscreen)
                            startInfo.ArgumentList.Add(part);
                        }
                }
            }

            var process = Process.Start(startInfo);

            if (process != null)
            {
                UpdateStats(item);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fehler beim Starten: {ex.Message}");
        }
    }

    private void UpdateStats(MediaItem item)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
    }
}