using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Services;

public class MetadataService
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient;

    public MetadataService(AppSettings settings, HttpClient httpClient)
    {
        _settings = settings;
        _httpClient = httpClient;
    }

    // Factory-Methode: Holt den Provider basierend auf der ID des Scraper-Profils
    public IMetadataProvider? GetProvider(string scraperConfigId)
    {
        var config = _settings.Scrapers.FirstOrDefault(s => s.Id == scraperConfigId);
        if (config == null) return null;

        switch (config.Type)
        {
            case ScraperType.IGDB:
                return new IgdbProvider(config, _httpClient);
            
            case ScraperType.EmuMovies:
                return new EmuMoviesProvider(config, _httpClient);
            
            case ScraperType.OpenLibrary:
                return new OpenLibraryProvider(config, _httpClient);
            
            case ScraperType.TMDB:
                return new TmdbProvider(config, _httpClient);
            
            case ScraperType.GoogleBooks:
                return new GoogleBooksProvider(config, _httpClient);
            
            case ScraperType.ComicVine:
                return new ComicVineProvider(config, _httpClient);
            
            default:
                return null;
        }
    }

    // Holt alle passenden Scraper für einen bestimmten Typ (z.B. nur Game-Scraper)
    // Das ist nützlich für Dropdowns in der UI
    public List<ScraperConfig> GetConfigsForType(ScraperType type)
    {
        return _settings.Scrapers.Where(s => s.Type == type).ToList();
    }
}