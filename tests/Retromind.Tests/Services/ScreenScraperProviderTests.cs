using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScreenScraperProviderTests
{
    [Fact]
    public async Task ConnectAndSearch_EncodeCredentialsAndParseLocalizedGameData()
    {
        var requestedUris = new List<Uri>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUris.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/ssuserInfos.php", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"response":{"ssuser":{"id":"member name"}}}""");
            }

            return JsonResponse(
                """
                {
                  "response": {
                    "jeux": [
                      {
                        "id": "3",
                        "nom": "Internal title",
                        "noms": [
                          { "region": "us", "text": "Test Game US" },
                          { "region": "de", "text": "Testspiel" }
                        ],
                        "synopsis": [
                          { "langue": "en", "text": "English synopsis" },
                          { "langue": "de", "text": "Deutsche Beschreibung" }
                        ],
                        "systeme": { "id": "1", "nom": "Mega Drive" },
                        "developpeur": "Developer",
                        "editeur": "Publisher",
                        "joueurs": "1-2",
                        "note": "16",
                        "dates": [
                          { "region": "us", "text": "1992-11-24" },
                          { "region": "de", "text": "1992-11-26" }
                        ],
                        "genres": [
                          {
                            "principale": "1",
                            "noms": [
                              { "langue": "en", "text": "Platform" },
                              { "langue": "de", "text": "Plattform" }
                            ]
                          }
                        ],
                        "medias": [
                          { "type": "box-2D", "region": "us", "url": "https://media.example/us-box.png" },
                          { "type": "box-2D", "region": "de", "url": "https://media.example/de-box.png" },
                          { "type": "fanart", "region": "wor", "url": "https://media.example/fanart.jpg" },
                          { "type": "ss", "region": "wor", "url": "https://media.example/screenshot.png" },
                          { "type": "wheel-hd", "region": "wor", "url": "https://media.example/logo.png" },
                          { "type": "marquee", "region": "wor", "url": "https://media.example/marquee.png" },
                          { "type": "bezel-16-9", "region": "wor", "url": "https://media.example/bezel.png" },
                          { "type": "cpanel", "region": "wor", "url": "https://media.example/control-panel.png" }
                        ]
                      }
                    ]
                  }
                }
                """);
        }));

        var provider = new ScreenScraperProvider(
            new ScraperConfig
            {
                Type = ScraperType.ScreenScraper,
                Username = "member name",
                Password = "member password",
                Language = "de-DE"
            },
            httpClient,
            new ScreenScraperApplicationCredentials("developer id", "developer+/password"));

        Assert.True(await provider.ConnectAsync());
        var result = Assert.Single(await provider.SearchAsync("Test & Game"));

        Assert.Equal("ScreenScraper", result.Source);
        Assert.Equal("3", result.Id);
        Assert.Equal("Testspiel", result.Title);
        Assert.Equal("Deutsche Beschreibung", result.Description);
        Assert.Equal("Mega Drive", result.Platform);
        Assert.Equal("Developer", result.Developer);
        Assert.Equal("Publisher", result.Publisher);
        Assert.Equal("Plattform", result.Genre);
        Assert.Equal("1-2", result.MaxPlayers);
        Assert.Equal(80, result.Rating);
        Assert.Equal(new DateTime(1992, 11, 26), result.ReleaseDate);
        Assert.Equal("https://media.example/de-box.png", result.CoverUrl);
        Assert.Equal("https://media.example/fanart.jpg", result.WallpaperUrl);
        Assert.Equal("https://media.example/screenshot.png", result.ScreenshotUrl);
        Assert.Equal("https://media.example/logo.png", result.LogoUrl);
        Assert.Equal("https://media.example/marquee.png", result.MarqueeUrl);
        Assert.Equal("https://media.example/bezel.png", result.BezelUrl);
        Assert.Equal("https://media.example/control-panel.png", result.ControlPanelUrl);

        Assert.Equal(2, requestedUris.Count);
        var searchQuery = requestedUris[1].Query;
        Assert.Contains("devid=developer%20id", searchQuery, StringComparison.Ordinal);
        Assert.Contains("devpassword=developer%2B%2Fpassword", searchQuery, StringComparison.Ordinal);
        Assert.Contains("softname=Retromind", searchQuery, StringComparison.Ordinal);
        Assert.Contains("ssid=member%20name", searchQuery, StringComparison.Ordinal);
        Assert.Contains("sspassword=member%20password", searchQuery, StringComparison.Ordinal);
        Assert.Contains("recherche=Test%20%26%20Game", searchQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_RejectsIncompleteCredentialPairsWithoutNetworkRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{}");
        }));
        var provider = new ScreenScraperProvider(
            new ScraperConfig
            {
                Username = "member"
            },
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        Assert.False(await provider.ConnectAsync());
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task ConnectAsync_RejectsDifferentMemberReturnedByService()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse("""{"response":{"ssuser":{"id":"different-member"}}}""")));
        var provider = new ScreenScraperProvider(
            new ScraperConfig
            {
                Username = "expected-member",
                Password = "member-password"
            },
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        Assert.False(await provider.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAndSearch_WithoutMemberAccount_ParsesDocumentedObjectShape()
    {
        var requestedPaths = new List<string>();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/ssinfraInfos.php", StringComparison.Ordinal))
                return JsonResponse("""{"response":{"serveurs":{"cpu1":"10"}}}""");

            return JsonResponse(
                """
                {
                  "response": {
                    "jeux": [
                      {
                        "id": "42",
                        "nom": "Internal title",
                        "noms": {
                          "nom_ss": "Internal title",
                          "nom_de": "Objekt-Testspiel",
                          "nom_us": "Object Test Game"
                        },
                        "synopsis": {
                          "synopsis_de": "Objektbeschreibung",
                          "synopsis_en": "Object description"
                        },
                        "systeme": { "id": "1", "nom": "Mega Drive" },
                        "note": "15",
                        "dates": {
                          "date_de": "1993-02-03",
                          "date_us": "1993-02-01"
                        },
                        "genres": {
                          "genres_id": [
                            { "genre_id": "2", "principale": "0" },
                            { "genre_id": "1", "principale": "1" }
                          ],
                          "genres_de": [
                            { "genre_de": "Nebenrolle" },
                            { "genre_de": "Action" }
                          ]
                        },
                        "medias": {
                          "media_screenshot": "https://media.example/object-screenshot.png",
                          "media_fanart": "https://media.example/object-fanart.jpg",
                          "media_marquee": "https://media.example/object-marquee.png",
                          "media_wheels": {
                            "media_wheel_de": "https://media.example/object-logo.png"
                          },
                          "media_boitiers": {
                            "media_boitiers_2d": {
                              "media_boitier_2d_de": "https://media.example/object-box.png"
                            }
                          },
                          "media_bezels": {
                            "media_bezel16-9": {
                              "media_bezel16-9_wor": "https://media.example/object-bezel.png"
                            }
                          }
                        }
                      }
                    ]
                  }
                }
                """);
        }));

        var provider = new ScreenScraperProvider(
            new ScraperConfig
            {
                Language = "de-DE"
            },
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        Assert.True(await provider.ConnectAsync());
        var result = Assert.Single(await provider.SearchAsync("Object Test"));

        Assert.Equal("Objekt-Testspiel", result.Title);
        Assert.Equal("Objektbeschreibung", result.Description);
        Assert.Equal("Action", result.Genre);
        Assert.Equal(new DateTime(1993, 2, 3), result.ReleaseDate);
        Assert.Equal(75, result.Rating);
        Assert.Equal("https://media.example/object-box.png", result.CoverUrl);
        Assert.Equal("https://media.example/object-fanart.jpg", result.WallpaperUrl);
        Assert.Equal("https://media.example/object-screenshot.png", result.ScreenshotUrl);
        Assert.Equal("https://media.example/object-logo.png", result.LogoUrl);
        Assert.Equal("https://media.example/object-marquee.png", result.MarqueeUrl);
        Assert.Equal("https://media.example/object-bezel.png", result.BezelUrl);
        Assert.Equal(new[] { "/api2/ssinfraInfos.php", "/api2/jeuRecherche.php" }, requestedPaths);
    }

    [Fact]
    public async Task SearchAsync_NetworkFailureDoesNotExposeCredentials()
    {
        const string developerPassword = "developer-secret";
        const string memberPassword = "member-secret";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException($"Failed URI contained {developerPassword} and {memberPassword}")));
        var provider = new ScreenScraperProvider(
            new ScraperConfig
            {
                Username = "member",
                Password = memberPassword
            },
            httpClient,
            new ScreenScraperApplicationCredentials("developer", developerPassword));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SearchAsync("game"));

        Assert.DoesNotContain(developerPassword, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(memberPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_WithoutApplicationCredentials_DoesNotSendRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{}");
        }));
        var provider = new ScreenScraperProvider(
            new ScraperConfig(),
            httpClient,
            applicationCredentials: null);

        Assert.False(await provider.ConnectAsync());
        Assert.Equal(0, requestCount);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
