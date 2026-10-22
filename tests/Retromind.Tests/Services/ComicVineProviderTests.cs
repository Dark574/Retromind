using System.Net;
using System.Text;
using System.Web;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ComicVineProviderTests
{
    [Fact]
    public async Task SearchAsync_IssueSearchServerError_RecoversIssuesAndVolumeArtwork()
    {
        var requestedResources = new List<string?>();
        string? issueFilter = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (request.RequestUri.AbsolutePath.EndsWith("/api/issues/", StringComparison.Ordinal))
            {
                issueFilter = query["filter"];
                return JsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 692216,
                          "resource_type": "issue",
                          "name": "Band 1",
                          "issue_number": "1",
                          "volume": { "id": 142910, "name": "I.S.S. Snipers" },
                          "image": {
                            "medium_url": "https://images.example/issue-cover.jpg",
                            "screen_url": "https://images.example/issue-screen.jpg"
                          }
                        }
                      ]
                    }
                    """);
            }

            var resources = query["resources"];
            requestedResources.Add(resources);

            return resources switch
            {
                "volume,issue" => HtmlServerError(),
                "volume" => JsonResponse(
                    """
                    {
                      "results": [
                        {
                          "id": 142910,
                          "resource_type": "volume",
                          "name": "I.S.S. Snipers",
                          "start_year": "2022",
                          "image": {
                            "medium_url": "https://comicvine.gamespot.com/a/uploads/scale_medium/6373148-blank.png",
                            "screen_url": "https://comicvine.gamespot.com/a/uploads/screen_medium/6373148-blank.png"
                          }
                        }
                      ]
                    }
                    """),
                "issue" => HtmlServerError(),
                _ => throw new InvalidOperationException($"Unexpected resource query: {resources}")
            };
        }));
        var provider = new ComicVineProvider(
            new ScraperConfig { ApiKey = "test-key" },
            client);

        var results = await provider.SearchAsync("ISS Snipers 01 - Reid Eckart");
        var volume = Assert.Single(results, result => result.ReleaseType == "volume");
        var issue = Assert.Single(results, result => result.ReleaseType == "issue");

        Assert.Equal("142910", volume.Id);
        Assert.Equal("I.S.S. Snipers", volume.Title);
        Assert.Equal("https://images.example/issue-cover.jpg", volume.CoverUrl);
        Assert.Equal("https://images.example/issue-screen.jpg", volume.WallpaperUrl);
        Assert.Equal("692216", issue.Id);
        Assert.Equal("142910", issue.CustomFields["VolumeId"]);
        Assert.Equal(["volume,issue", "volume", "issue"], requestedResources);
        Assert.Equal("volume:142910", issueFilter);
    }

    [Fact]
    public async Task SearchAsync_AllResourceRequestsFail_DoesNotExposeHtmlBody()
    {
        using var client = new HttpClient(new StubHandler(_ => HtmlServerError()));
        var provider = new ComicVineProvider(
            new ScraperConfig { ApiKey = "test-key" },
            client);

        var exception = await Assert.ThrowsAsync<Exception>(
            () => provider.SearchAsync("ISS Snipers"));

        Assert.Contains("unexpected HTML error page", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<!doctype html>", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_SuccessfulCombinedRequest_DoesNotUseFallback()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{\"results\":[]}");
        }));
        var provider = new ComicVineProvider(
            new ScraperConfig { ApiKey = "test-key" },
            client);

        var results = await provider.SearchAsync("Batman");

        Assert.Empty(results);
        Assert.Equal(1, requestCount);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage HtmlServerError() =>
        new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                "<!doctype html><title>500: Internal Server Error - Comic Vine</title>",
                Encoding.UTF8,
                "text/html")
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
