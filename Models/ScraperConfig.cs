using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Resources; 

namespace Retromind.Models;

/// <summary>
/// Configuration profile for a metadata provider (Scraper).
/// Stores credentials and preferences for services like TMDB, IGDB, etc.
/// </summary>
public partial class ScraperConfig : ObservableObject
{
    /// <summary>
    /// Unique identifier for this profile.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // --- Name Logic (UX Magic) ---
    // We automatically update the name based on the selected Type, 
    // UNTIL the user manually edits the name. 

    private string _name = Strings.Metadata_NewScraper; // Localized default
    private bool _isNameCustomized = false;

    /// <summary>
    /// Display name of the profile. 
    /// Auto-updates based on Type unless manually customized.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                // User manually typed something -> stop auto-updating
                _isNameCustomized = true;
            }
        }
    }

    [ObservableProperty] 
    private ScraperType _type = ScraperType.None;

    // Hook into the PropertyChanged event of the Type property
    partial void OnTypeChanged(ScraperType value)
    {
        // If the user hasn't customized the name yet, 
        // auto-set it to the new type (e.g. "IGDB").
        if (!_isNameCustomized)
        {
            // Set field directly to bypass the "IsCustomized" logic in the setter
            _name = value.ToString();
            OnPropertyChanged(nameof(Name));
        }
    }

    // --- Credentials ---
    // Note: Stored in plain text in settings.json. 
    // Sufficient for local hobby usage, but not secure for enterprise environments.

    [ObservableProperty] private string? _apiKey;
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private string? _clientId;
    [ObservableProperty] private string? _clientSecret;
    
    /// <summary>
    /// Preferred language for metadata (e.g. "de-DE", "en-US").
    /// </summary>
    [ObservableProperty] private string _language = "en-US";
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