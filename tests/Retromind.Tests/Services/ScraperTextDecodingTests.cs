using System.Net;
using System.Text;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScraperTextDecodingTests
{
    private const string EncodedTitle = "Tangled &quot;Tales&quot; &amp; &#39;M&auml;rchen&#39;";
    private const string DecodedTitle = "Tangled \"Tales\" & 'Märchen'";
    private const string EncodedDescription = "&quot;Tangled Tales&quot; follows a wizard&#39;s apprentice.\nÄrger &amp; Spaß &#x1F9D9; &lt;3";
    private const string DecodedDescription = "\"Tangled Tales\" follows a wizard's apprentice.\nÄrger & Spaß 🧙 <3";
    private const string EncodedCompany = "M&uuml;ller &amp; S&ouml;hne";
    private const string DecodedCompany = "Müller & Söhne";
    private const string OpaqueId = "99&amp;id";
    private const string MediaUrl = "https://images.example/cover.jpg?first=1&amp;second=2";

    [Theory]
    [InlineData(ScraperType.ScreenScraper)]
    [InlineData(ScraperType.IGDB)]
    [InlineData(ScraperType.TheGamesDB)]
    [InlineData(ScraperType.EmuMovies)]
    [InlineData(ScraperType.TMDB)]
    [InlineData(ScraperType.GoogleBooks)]
    [InlineData(ScraperType.OpenLibrary)]
    [InlineData(ScraperType.ComicVine)]
    [InlineData(ScraperType.SteamGridDB)]
    public async Task SearchAsync_DecodesDisplayTextWhilePreservingOpaqueIdsAndMediaUrls(ScraperType type)
    {
        using var client = CreateClient(type, EncodedTitle, EncodedDescription);
        var provider = CreateProvider(type, client);

        var result = Assert.Single(await provider.SearchAsync("Tangled Tales"));

        Assert.Equal(DecodedTitle, result.Title);
        Assert.StartsWith(DecodedTitle, result.DisplayTitle, StringComparison.Ordinal);
        Assert.EndsWith(OpaqueId, result.Id, StringComparison.Ordinal);
        if (type is not (ScraperType.OpenLibrary or ScraperType.SteamGridDB))
            Assert.Equal(DecodedDescription, result.Description);
        if (type is ScraperType.ScreenScraper or ScraperType.IGDB or ScraperType.GoogleBooks or
            ScraperType.OpenLibrary or ScraperType.ComicVine)
            Assert.Equal(DecodedCompany, result.Publisher);
        if (type is ScraperType.ScreenScraper or ScraperType.IGDB or ScraperType.GoogleBooks or ScraperType.ComicVine)
            Assert.Equal(MediaUrl, result.CoverUrl);
        if (type is ScraperType.GoogleBooks or ScraperType.OpenLibrary or ScraperType.EmuMovies)
            Assert.Contains(DecodedCompany, result.DisplayTitle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScraperType.ScreenScraper)]
    [InlineData(ScraperType.IGDB)]
    [InlineData(ScraperType.TheGamesDB)]
    [InlineData(ScraperType.EmuMovies)]
    [InlineData(ScraperType.TMDB)]
    [InlineData(ScraperType.GoogleBooks)]
    [InlineData(ScraperType.OpenLibrary)]
    [InlineData(ScraperType.ComicVine)]
    [InlineData(ScraperType.SteamGridDB)]
    public async Task SearchAsync_DecodesOnlyOneLayerAndKeepsUnknownEntities(ScraperType type)
    {
        using var client = CreateClient(type, "Literal &amp;quot; &unknown;", "Keep &amp;lt;tag&amp;gt;");
        var result = Assert.Single(await CreateProvider(type, client).SearchAsync("Literal"));

        Assert.Equal("Literal &quot; &unknown;", result.Title);
        Assert.StartsWith("Literal &quot; &unknown;", result.DisplayTitle, StringComparison.Ordinal);
        if (type is not (ScraperType.OpenLibrary or ScraperType.SteamGridDB))
            Assert.Equal("Keep &lt;tag&gt;", result.Description);
    }

    private static IMetadataProvider CreateProvider(ScraperType type, HttpClient client)
    {
        var config = new ScraperConfig
        {
            ApiKey = "test-key", ClientId = "test-client", ClientSecret = "test-secret",
            Username = "test-user", Password = "test-password", Language = "en-US"
        };
        return type switch
        {
            ScraperType.ScreenScraper => new ScreenScraperProvider(config, client,
                new ScreenScraperApplicationCredentials("developer", "password")),
            ScraperType.IGDB => new IgdbProvider(config, client),
            ScraperType.TheGamesDB => new TheGamesDbProvider(config, client),
            ScraperType.EmuMovies => new EmuMoviesProvider(config, client),
            ScraperType.TMDB => new TmdbProvider(config, client),
            ScraperType.GoogleBooks => new GoogleBooksProvider(config, client),
            ScraperType.OpenLibrary => new OpenLibraryProvider(config, client),
            ScraperType.ComicVine => new ComicVineProvider(config, client),
            ScraperType.SteamGridDB => new SteamGridDbProvider(config, client),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static HttpClient CreateClient(ScraperType type, string title, string description) =>
        new(new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
                return new { access_token = "test-token", expires_in = 3600 };
            if (request.RequestUri.AbsolutePath == "/api/login")
                return new { Result = "Success", Session = "test-session" };
            return CreatePayload(type, title, description);
        }));

    private static object CreatePayload(ScraperType type, string title, string description) => type switch
    {
        ScraperType.ScreenScraper => new
        {
            response = new { jeux = new[] { new
            {
                id = OpaqueId, nom = title, synopsis = description,
                developpeur = EncodedCompany, editeur = EncodedCompany,
                medias = new[] { new { type = "box-2D", url = MediaUrl, region = "wor" } }
            } } }
        },
        ScraperType.IGDB => new[] { new
        {
            id = OpaqueId, name = title, summary = description,
            cover = new { url = MediaUrl },
            involved_companies = new[] { new { company = new { name = EncodedCompany }, publisher = true } }
        } },
        ScraperType.TheGamesDB => new
        {
            data = new { games = new[] { new { id = OpaqueId, game_title = title, overview = description } } }
        },
        ScraperType.EmuMovies => new
        {
            Games = new[] { new { ID = OpaqueId, Name = title, Description = description, System = EncodedCompany } }
        },
        ScraperType.TMDB => new
        {
            total_pages = 1,
            results = new[] { new { id = OpaqueId, title, overview = description, media_type = "movie" } }
        },
        ScraperType.GoogleBooks => new
        {
            totalItems = 1,
            items = new[] { new { id = OpaqueId, volumeInfo = new
            {
                title, description, publisher = EncodedCompany, authors = new[] { EncodedCompany },
                imageLinks = new { thumbnail = MediaUrl }
            } } }
        },
        ScraperType.OpenLibrary => new
        {
            docs = new[] { new
            {
                key = "/works/" + OpaqueId, title, title_sort = title,
                author_name = new[] { EncodedCompany }, publisher = new[] { EncodedCompany }
            } }
        },
        ScraperType.ComicVine => new
        {
            results = new[] { new
            {
                id = OpaqueId, name = title, description = "<p>" + description + "</p>", resource_type = "volume",
                publisher = new { name = EncodedCompany }, image = new { medium_url = MediaUrl }
            } }
        },
        ScraperType.SteamGridDB => new { data = new[] { new { id = OpaqueId, name = title } } },
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, object> payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload(request)), Encoding.UTF8, "application/json")
            });
    }
}
