using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    // Cache providers per scraper config ID so we can reuse connections/auth state.
    private readonly ConcurrentDictionary<string, IMetadataProvider> _providerCache = new();

    // Ensure ConnectAsync is executed at most once per provider (even with concurrent callers).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new();

    public MetadataService(AppSettings settings, HttpClient httpClient)
    {
        _settings = settings;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates and returns a scraper provider instance based on the configuration ID.
    /// Note: This method does not guarantee that ConnectAsync() has been called.
    /// Prefer GetProviderAsync() for runtime usage.
    /// </summary>
    public IMetadataProvider? GetProvider(string scraperConfigId)
    {
        if (string.IsNullOrWhiteSpace(scraperConfigId))
            return null;

        // Fast path: reuse cached provider.
        if (_providerCache.TryGetValue(scraperConfigId, out var cached))
            return cached;

        var config = _settings.Scrapers.FirstOrDefault(s => s.Id == scraperConfigId);
        if (config == null) return null;

        var provider = CreateProvider(config);
        if (provider == null) return null;

        _providerCache.TryAdd(scraperConfigId, provider);
        return provider;
    }

    /// <summary>
    /// Returns a provider and ensures it is connected (ConnectAsync called once).
    /// Use this for actual scraping/search operations.
    /// </summary>
    public async Task<IMetadataProvider?> GetProviderAsync(string scraperConfigId, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(scraperConfigId);
        if (provider == null) return null;

        // Serialize ConnectAsync per config ID.
        var gate = _connectGates.GetOrAdd(scraperConfigId, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // We intentionally do not cache the "connected" flag here because provider implementations
            // may reconnect internally. ConnectAsync should be idempotent or cheap after the first run.
            // If a provider returns false, we treat it as unusable for this session.
            var ok = await provider.ConnectAsync().ConfigureAwait(false);
            return ok ? provider : null;
        }
        finally
        {
            gate.Release();
        }
    }

    private IMetadataProvider? CreateProvider(ScraperConfig config)
    {
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