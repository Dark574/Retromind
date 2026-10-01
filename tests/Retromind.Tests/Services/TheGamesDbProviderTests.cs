using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class TheGamesDbProviderTests
{
    [Fact]
    public async Task BulkSearch_UsesOneRankedPage_AndDefersSupplementalMetadata()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return JsonResponse(SearchPayload(remaining: 100));
        }));
        var provider = CreateProvider(client);

        var result = Assert.Single(await provider.SearchForBulkAsync("Test Game"));

        Assert.Single(requests);
        Assert.EndsWith("/Games/ByGameName", requests[0].AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("page=1", requests[0].Query, StringComparison.Ordinal);
        Assert.Equal("PC", result.Platform);
        Assert.Null(result.Genre);
        Assert.Null(result.Developer);
        Assert.Null(result.Publisher);
    }

    [Fact]
    public async Task EnrichAsync_LoadsNamesOnlyForAcceptedResult_AndCachesThem()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Genres/ByGenreID", StringComparison.Ordinal))
                return JsonResponse(NamePayload("genres", "10", "Action"));
            if (path.EndsWith("/Developers/ByDeveloperID", StringComparison.Ordinal))
                return JsonResponse(NamePayload("developers", "20", "Developer"));
            if (path.EndsWith("/Publishers/ByPublisherID", StringComparison.Ordinal))
                return JsonResponse(NamePayload("publishers", "30", "Publisher"));

            return JsonResponse(SearchPayload(remaining: 100));
        }));
        var provider = CreateProvider(client);

        var first = Assert.Single(await provider.SearchForBulkAsync("First"));
        await provider.EnrichAsync(first);
        var second = Assert.Single(await provider.SearchForBulkAsync("Second"));
        await provider.EnrichAsync(second);

        Assert.Equal("Action", first.Genre);
        Assert.Equal("Developer", first.Developer);
        Assert.Equal("Publisher", first.Publisher);
        Assert.Equal("Action", second.Genre);
        Assert.Equal("Developer", second.Developer);
        Assert.Equal("Publisher", second.Publisher);
        Assert.Equal(2, requests.Count(uri => uri.AbsolutePath.EndsWith("/Games/ByGameName", StringComparison.Ordinal)));
        Assert.Equal(1, requests.Count(uri => uri.AbsolutePath.EndsWith("/Genres/ByGenreID", StringComparison.Ordinal)));
        Assert.Equal(1, requests.Count(uri => uri.AbsolutePath.EndsWith("/Developers/ByDeveloperID", StringComparison.Ordinal)));
        Assert.Equal(1, requests.Count(uri => uri.AbsolutePath.EndsWith("/Publishers/ByPublisherID", StringComparison.Ordinal)));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.EndsWith("/Games/Images", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportedExhaustedAllowance_BlocksFurtherRequests()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse(SearchPayload(remaining: 0));
        }));
        var provider = CreateProvider(client);

        Assert.Single(await provider.SearchForBulkAsync("Last permitted request"));
        var exception = await Assert.ThrowsAsync<MetadataQuotaExceededException>(
            () => provider.SearchForBulkAsync("Must not be sent"));

        Assert.Equal(1, requestCount);
        Assert.Contains("monthly request allowance", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForbiddenApiResponse_StopsBulkInsteadOfRepeatingRequests()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse(
                """{"code":403,"status":"API key allowance exceeded"}""",
                HttpStatusCode.Forbidden);
        }));
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<MetadataQuotaExceededException>(
            () => provider.SearchForBulkAsync("Rejected request"));

        Assert.Equal(1, requestCount);
        Assert.Contains("bulk scraping was stopped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TheGamesDbProvider CreateProvider(HttpClient client) =>
        new(new ScraperConfig
        {
            Type = ScraperType.TheGamesDB,
            ApiKey = "secret-key",
            Language = "en"
        }, client);

    private static string SearchPayload(int remaining) =>
        $$"""
        {
          "code": 200,
          "status": "Success",
          "remaining_monthly_allowance": {{remaining}},
          "extra_allowance": 0,
          "allowance_refresh_timer": 12345,
          "data": {
            "games": [
              {
                "id": 1,
                "game_title": "Test Game",
                "platform": 5,
                "genres": [10],
                "developers": [20],
                "publishers": [30]
              }
            ]
          },
          "include": {
            "platform": {
              "data": { "5": { "id": 5, "name": "PC" } }
            },
            "boxart": {
              "base_url": { "original": "https://images.example" },
              "data": {
                "1": [
                  { "type": "boxart", "side": "front", "filename": "cover.jpg" },
                  { "type": "fanart", "filename": "fanart.jpg" },
                  { "type": "screenshot", "filename": "screen.jpg" },
                  { "type": "clearlogo", "filename": "logo.png" },
                  { "type": "marquee", "filename": "marquee.png" }
                ]
              }
            }
          }
        }
        """;

    private static string NamePayload(string collection, string id, string name) =>
        $$"""
        {
          "code": 200,
          "status": "Success",
          "remaining_monthly_allowance": 100,
          "extra_allowance": 0,
          "data": {
            "{{collection}}": {
              "{{id}}": { "id": {{id}}, "name": "{{name}}" }
            }
          }
        }
        """;

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
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
