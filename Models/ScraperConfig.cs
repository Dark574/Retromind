using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

public partial class ScraperConfig : ObservableObject
{
    // Konstante für den Startwert
    public const string DefaultName = "Neuer Scraper"; 

    public string Id { get; set; } = Guid.NewGuid().ToString();

    // --- NAME LOGIK START ---
    private string _name = DefaultName;
    private bool _isNameCustomized = false; // Das ist unser "Merkzettel"

    public string Name
    {
        get => _name;
        set
        {
            // Wenn der Wert vom UI (TextBox) kommt...
            if (SetProperty(ref _name, value))
            {
                // ... markieren wir ihn als "Benutzerdefiniert"
                _isNameCustomized = true;
            }
        }
    }
    // ÄNDERUNG: Standard ist jetzt "None" (Keine Auswahl)
    private ScraperType _type = ScraperType.None;

    public ScraperType Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                // ROBUSTE LOGIK:
                // Wir ändern den Namen automatisch, wenn er noch NICHT vom Benutzer angepasst wurde.
                if (!_isNameCustomized)
                {
                    // Wir setzen das Feld direkt (umgehen den Setter), 
                    // damit _isNameCustomized NICHT auf true springt.
                    _name = value.ToString();
                    
                    // Aber wir sagen der UI Bescheid!
                    OnPropertyChanged(nameof(Name));
                }
            }
        }
    }

    // ... Rest der Properties wie gehabt ...
    [ObservableProperty] private string? _apiKey;
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private string? _clientId;
    [ObservableProperty] private string? _clientSecret;
    [ObservableProperty] private string _language = "de-DE";
}

public enum ScraperType
{
    None,
    TMDB,
    IGDB,
    EmuMovies,
    GoogleBooks,
    OpenLibrary,
    ComicVine
}