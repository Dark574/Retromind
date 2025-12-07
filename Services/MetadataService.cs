using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Services;

/// <summary>
/// Service acting as a factory for Metadata Providers (Scrapers).
/// Resolves the correct implementation based on user configuration.
/// </summary>
public class MetadataService
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;

    public MetadataService(AppSettings settings, HttpClient httpClient)
    {
        _settings = settings;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates and returns a scraper provider instance based on the configuration ID.
    /// </summary>
    /// <param name="scraperConfigId">The unique ID of the scraper configuration profile.</param>
    /// <returns>An initialized provider implementing <see cref="IMetadataProvider"/> or null if not found.</returns>
    public IMetadataProvider? GetProvider(string scraperConfigId)
    {
        var config = _settings.Scrapers.FirstOrDefault(s => s.Id == scraperConfigId);
        if (config == null) return null;

        // Modern switch expression for cleaner factory logic.
        // Note: In a larger system, this could be replaced by a DI container resolving keyed services.
        return config.Type switch
        {
            ScraperType.IGDB        => new IgdbProvider(config, _httpClient),
            ScraperType.EmuMovies   => new EmuMoviesProvider(config, _httpClient),
            ScraperType.OpenLibrary => new OpenLibraryProvider(config, _httpClient),
            ScraperType.TMDB        => new TmdbProvider(config, _httpClient),
            ScraperType.GoogleBooks => new GoogleBooksProvider(config, _httpClient),
            ScraperType.ComicVine   => new ComicVineProvider(config, _httpClient),
            _                       => null
        };
    }

    /// <summary>
    /// Retrieves all configured scraper profiles of a specific type.
    /// Useful for populating UI selection lists (e.g., "Select a Game Scraper").
    /// </summary>
    public List<ScraperConfig> GetConfigsForType(ScraperType type)
    {
        return _settings.Scrapers
            .Where(s => s.Type == type)
            .ToList();
    }
}