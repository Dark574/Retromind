using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Models;
using Retromind.Services.GameIdentification;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScreenScraperProviderTests
{
    [Fact]
    public async Task IdentifyGameFileAsync_UsesSystemAndAllChecksums()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "response": {
                    "jeu": {
                      "id": "99",
                      "nom": "Internal title",
                      "noms": { "nom_de": "Erkanntes Spiel" },
                      "systeme": { "id": "12", "nom": "Game Boy Advance" },
                      "medias": {}
                    }
                  }
                }
                """);
        }));
        var fingerprintService = new StubFingerprintService(
            new GameFileFingerprint(
                123456,
                DateTime.UnixEpoch,
                "a1b2c3d4",
                "00112233445566778899aabbccddeeff",
                "00112233445566778899aabbccddeeff00112233"));
        var provider = new ScreenScraperProvider(
            new ScraperConfig { Language = "de-DE" },
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"),
            fingerprintService);

        var result = await provider.IdentifyGameFileAsync(
            "nintendo.game-boy-advance",
            "/games/Test Game.gba");

        Assert.NotNull(result);
        Assert.Equal("99", result.Id);
        Assert.Equal("Erkanntes Spiel", result.Title);
        Assert.Equal("Game Boy Advance", result.Platform);
        Assert.Equal("/games/Test Game.gba", fingerprintService.FilePath);
        Assert.NotNull(requestedUri);
        Assert.EndsWith("/jeuInfos.php", requestedUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("systemeid=12", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("romtype=rom", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("romnom=Test%20Game.gba", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("romtaille=123456", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("crc=A1B2C3D4", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("md5=00112233445566778899AABBCCDDEEFF", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("sha1=00112233445566778899AABBCCDDEEFF00112233", requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyGameFileAsync_NotFound_ReturnsNull()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        var provider = new ScreenScraperProvider(
            new ScraperConfig(),
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"),
            new StubFingerprintService(new GameFileFingerprint(
                1,
                DateTime.UnixEpoch,
                "00000000",
                new string('0', 32),
                new string('0', 40))));

        var result = await provider.IdentifyGameFileAsync(
            "sega.mega-drive",
            "/games/unknown.zip");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("sega.mega-drive", true)]
    [InlineData("sony.playstation-2", true)]
    [InlineData("unknown.system", false)]
    [InlineData(null, false)]
    public void SupportsGameSystem_UsesProviderSpecificMapping(string? gameSystemId, bool expected)
    {
        using var httpClient = new HttpClient(new StubHandler(_ => JsonResponse("{}")));
        var provider = new ScreenScraperProvider(
            new ScraperConfig(),
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        Assert.Equal(expected, provider.SupportsGameSystem(gameSystemId));
    }

    [Fact]
    public async Task SearchByGameSystemAsync_ConstrainsTitleSearchToMappedSystem()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {"response":{"jeux":[{"id":"7","nom":"Sonic","systeme":{"nom":"Mega Drive"}}]}}
                """);
        }));
        var provider = new ScreenScraperProvider(
            new ScraperConfig(),
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        var result = Assert.Single(await provider.SearchByGameSystemAsync(
            "Sonic",
            "sega.mega-drive"));

        Assert.Equal("Sonic", result.Title);
        Assert.NotNull(requestedUri);
        Assert.EndsWith("/jeuRecherche.php", requestedUri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("recherche=Sonic", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("systemeid=1", requestedUri.Query, StringComparison.Ordinal);
    }

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
                          { "type": "video", "region": "wor", "url": "https://media.example/gameplay.mp4" },
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
        Assert.Equal("https://media.example/gameplay.mp4", result.VideoUrl);
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
                          "media_video": "https://media.example/object-video.mp4",
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
        Assert.Equal("https://media.example/object-video.mp4", result.VideoUrl);
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

    [Theory]
    [InlineData(429)]
    [InlineData(430)]
    [InlineData(431)]
    public async Task SearchAsync_RequestAllowanceResponse_ReportsExhaustedQuota(int statusCode)
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)statusCode)));
        var provider = new ScreenScraperProvider(
            new ScraperConfig(),
            httpClient,
            new ScreenScraperApplicationCredentials("developer", "password"));

        var exception = await Assert.ThrowsAsync<MetadataQuotaExceededException>(
            () => provider.SearchAsync("game"));

        Assert.Contains("request limit", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private sealed class StubFingerprintService(GameFileFingerprint fingerprint)
        : IGameFileFingerprintService
    {
        public string? FilePath { get; private set; }

        public Task<GameFileFingerprint> CalculateAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FilePath = filePath;
            return Task.FromResult(fingerprint);
        }
    }
}
