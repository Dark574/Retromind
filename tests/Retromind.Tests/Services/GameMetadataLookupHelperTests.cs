using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class GameMetadataLookupHelperTests
{
    [Fact]
    public async Task SearchAsync_ExactFileMatch_SkipsTitleSearch()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var provider = new StubGameProvider
            {
                IdentifiedResult = new ScraperSearchResult { Id = "42", Title = "Exact game" }
            };

            var result = await GameMetadataLookupHelper.SearchAsync(
                provider,
                "Approximate title",
                "sega.mega-drive",
                filePath);

            Assert.True(result.IsExactMatch);
            Assert.Equal("Exact game", Assert.Single(result.Results).Title);
            Assert.Equal(1, provider.IdentifyCalls);
            Assert.Equal(0, provider.SystemSearchCalls);
            Assert.Equal(0, provider.GenericSearchCalls);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SearchAsync_UnknownFile_UsesSystemConstrainedTitleFallback()
    {
        var filePath = Path.GetTempFileName();
        try
        {
            var provider = new StubGameProvider
            {
                SystemResults = new List<ScraperSearchResult>
                {
                    new() { Id = "43", Title = "Title fallback" }
                }
            };

            var result = await GameMetadataLookupHelper.SearchAsync(
                provider,
                "Game title",
                "sega.mega-drive",
                filePath);

            Assert.False(result.IsExactMatch);
            Assert.Equal("Title fallback", Assert.Single(result.Results).Title);
            Assert.Equal(1, provider.IdentifyCalls);
            Assert.Equal(1, provider.SystemSearchCalls);
            Assert.Equal(0, provider.GenericSearchCalls);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task SearchAsync_WithoutSupportedSystem_UsesGenericTitleSearch()
    {
        var provider = new StubGameProvider
        {
            SupportsSystems = false,
            GenericResults = new List<ScraperSearchResult>
            {
                new() { Id = "44", Title = "Generic fallback" }
            }
        };

        var result = await GameMetadataLookupHelper.SearchAsync(
            provider,
            "Game title",
            "unsupported",
            "/does/not/exist.rom");

        Assert.False(result.IsExactMatch);
        Assert.Equal("Generic fallback", Assert.Single(result.Results).Title);
        Assert.Equal(0, provider.IdentifyCalls);
        Assert.Equal(0, provider.SystemSearchCalls);
        Assert.Equal(1, provider.GenericSearchCalls);
    }

    private sealed class StubGameProvider : IMetadataProvider, IGameFileMetadataProvider
    {
        public bool SupportsSystems { get; init; } = true;
        public ScraperSearchResult? IdentifiedResult { get; init; }
        public List<ScraperSearchResult> SystemResults { get; init; } = new();
        public List<ScraperSearchResult> GenericResults { get; init; } = new();

        public int IdentifyCalls { get; private set; }
        public int SystemSearchCalls { get; private set; }
        public int GenericSearchCalls { get; private set; }

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool SupportsGameSystem(string? gameSystemId) => SupportsSystems;

        public Task<ScraperSearchResult?> IdentifyGameFileAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            IdentifyCalls++;
            return Task.FromResult(IdentifiedResult);
        }

        public Task<List<ScraperSearchResult>> SearchByGameSystemAsync(
            string query,
            string gameSystemId,
            CancellationToken cancellationToken = default)
        {
            SystemSearchCalls++;
            return Task.FromResult(SystemResults);
        }

        public Task<List<ScraperSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            GenericSearchCalls++;
            return Task.FromResult(GenericResults);
        }
    }
}
