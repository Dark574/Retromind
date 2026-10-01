using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScraperCanonicalTitleTests
{
    [Fact]
    public async Task GoogleBooks_KeepsAuthorAndSubtitleOutOfMatchingTitle()
    {
        using var client = CreateClient(
            """
            {
              "totalItems": 1,
              "items": [
                {
                  "id": "dune",
                  "volumeInfo": {
                    "title": "Dune",
                    "subtitle": "The Desert Planet",
                    "authors": ["Frank Herbert"]
                  }
                }
              ]
            }
            """);
        var provider = new GoogleBooksProvider(new ScraperConfig(), client);

        var result = Assert.Single(await provider.SearchAsync("Dune"));

        Assert.Equal("Dune", result.Title);
        Assert.Equal("Dune - The Desert Planet (Frank Herbert)", result.DisplayTitle);
        AssertAutomaticMatch("Dune", result);
    }

    [Fact]
    public async Task OpenLibrary_KeepsAuthorOutOfMatchingTitle()
    {
        using var client = CreateClient(
            """
            {
              "docs": [
                {
                  "key": "/works/OL893415W",
                  "title": "Dune",
                  "author_name": ["Frank Herbert"]
                }
              ]
            }
            """);
        var provider = new OpenLibraryProvider(new ScraperConfig(), client);

        var result = Assert.Single(await provider.SearchAsync("Dune"));

        Assert.Equal("Dune", result.Title);
        Assert.Equal("Dune (Frank Herbert)", result.DisplayTitle);
        AssertAutomaticMatch("Dune", result);
    }

    [Fact]
    public async Task ComicVine_KeepsReleaseYearAndIssueNameOutOfMatchingTitles()
    {
        using var client = CreateClient(
            """
            {
              "results": [
                {
                  "id": 1,
                  "resource_type": "volume",
                  "name": "Batman",
                  "start_year": "1940"
                },
                {
                  "id": 2,
                  "resource_type": "issue",
                  "name": "The Chameleon Strikes",
                  "volume": { "name": "Amazing Spider-Man" },
                  "issue_number": "1"
                }
              ]
            }
            """);
        var provider = new ComicVineProvider(
            new ScraperConfig { ApiKey = "test-key" },
            client);

        var results = await provider.SearchAsync("Batman");
        var volume = results[0];
        var issue = results[1];

        Assert.Equal("Batman", volume.Title);
        Assert.Equal("Batman (1940)", volume.DisplayTitle);
        AssertAutomaticMatch("Batman", volume);

        Assert.Equal("Amazing Spider-Man #1", issue.Title);
        Assert.Equal("Amazing Spider-Man #1 - The Chameleon Strikes", issue.DisplayTitle);
        AssertAutomaticMatch("Amazing Spider-Man #1", issue);
    }

    [Fact]
    public void DisplayTitle_WithoutOverride_FallsBackToCanonicalTitle()
    {
        var result = new ScraperSearchResult { Title = "Canonical title" };

        Assert.Equal("Canonical title", result.DisplayTitle);
    }

    private static void AssertAutomaticMatch(string itemTitle, ScraperSearchResult result)
    {
        var decision = ScraperMatchEvaluator.SelectBestMatch(itemTitle, new[] { result });

        Assert.Equal(ScraperMatchStatus.Match, decision.Status);
        Assert.Same(result, decision.BestCandidate);
    }

    private static HttpClient CreateClient(string json) =>
        new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
