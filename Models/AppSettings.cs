using System.Collections.Generic;

namespace Retromind.Models;

public class AppSettings
{
    // Standardwerte setzen
    public double TreeColumnWidth { get; set; } = 250;

    public double DetailColumnWidth { get; set; } = 300;

    // aktueller eingestellter Zoom-Modus
    public double ItemWidth { get; set; } = 150;

    // Theme Einstellung
    public bool IsDarkTheme { get; set; } = false; // Standard: Light

    // ID des zuletzt gewählten Knotens
    public string? LastSelectedNodeId { get; set; }

    // ID des zuletzt gewählten Spiels (MediaItem)
    public string? LastSelectedMediaId { get; set; }

    // Liste der definierten Emulatoren
    public List<EmulatorConfig> Emulators { get; set; } = new();
    
    // Liste der Scraper-Profile
    public List<ScraperConfig> Scrapers { get; set; } = new();
}