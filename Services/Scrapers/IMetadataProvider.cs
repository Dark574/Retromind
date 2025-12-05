using System.Collections.Generic;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public interface IMetadataProvider
{
    // Initialisiert den Provider (z.B. Login durchführen)
    Task<bool> ConnectAsync();

    // Sucht nach Medien
    Task<List<ScraperSearchResult>> SearchAsync(string query);
}