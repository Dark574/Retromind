using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class SteamGridDbProviderTests
{
    [Fact]
    public async Task EnrichAsync_SelectiveRequest_UsesOnlyRequestedArtworkEndpoint()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath.Contains("/search/autocomplete/", StringComparison.Ordinal)
                ? JsonResponse("""{"data":[{"id":7,"name":"Test Game"}]}""")
                : JsonResponse("""{"data":[{"url":"https://images.example/logo.png","score":10}]}""");
        }));
        var provider = new SteamGridDbProvider(
            new ScraperConfig { ApiKey = "secret-key" },
            client);

        var result = Assert.Single(await provider.SearchAsync("Test Game"));
        await provider.EnrichAsync(result, new MetadataEnrichmentRequest { Logo = true });

        Assert.Equal("https://images.example/logo.png", result.LogoUrl);
        Assert.Null(result.CoverUrl);
        Assert.Null(result.WallpaperUrl);
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, uri => uri.AbsolutePath.Contains("/logos/game/", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.Contains("/grids/game/", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.Contains("/heroes/game/", StringComparison.Ordinal));
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
