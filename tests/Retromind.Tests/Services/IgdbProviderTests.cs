using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class IgdbProviderTests
{
    [Theory]
    [InlineData("nintendo.snes", true)]
    [InlineData("sega.mega-drive", true)]
    [InlineData("sony.playstation-2", true)]
    [InlineData("unknown.system", false)]
    [InlineData("microsoft.pc", true)]
    [InlineData("microsoft.ms-dos", true)]
    [InlineData("microsoft.windows", true)]
    [InlineData(null, false)]
    public void SupportsGameSystem_UsesProviderSpecificMapping(
        string? gameSystemId,
        bool expected)
    {
        using var httpClient = new HttpClient(
            new StubHandler((_, _) =>
                Task.FromResult(JsonResponse("[]"))));

        var provider = new IgdbProvider(
            new ScraperConfig(),
            httpClient);

        Assert.Equal(expected, provider.SupportsGameSystem(gameSystemId));
    }

    [Theory]
    [InlineData("nintendo.snes", "where platforms = (19);")]
    [InlineData("microsoft.pc", "where platforms = (6,13);")]
    public async Task SearchByGameSystemAsync_AddsMappedPlatformFilter(
        string gameSystemId,
        string expectedFilter)
    {
        string? sentQuery = null;

        using var httpClient = new HttpClient(
            new StubHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri?.Host == "id.twitch.tv")
                {
                    return JsonResponse(
                        """
                        {
                          "access_token": "test-token",
                          "expires_in": 3600
                        }
                        """);
                }

                sentQuery = await request.Content!
                    .ReadAsStringAsync(cancellationToken);

                return JsonResponse(
                    """
                    [
                      {
                        "id": 19,
                        "name": "Super Mario World",
                        "platforms": [
                          { "name": "Super Nintendo Entertainment System" }
                        ]
                      }
                    ]
                    """);
            }));

        var provider = new IgdbProvider(
            new ScraperConfig
            {
                ClientId = "test-client",
                ClientSecret = "test-secret"
            },
            httpClient);

        var result = Assert.Single(
            await provider.SearchByGameSystemAsync(
                "Super Mario World",
                gameSystemId));

        Assert.Equal("Super Mario World", result.Title);
        Assert.NotNull(sentQuery);
        Assert.Contains(
            expectedFilter,
            sentQuery,
            StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _handler;

        public StubHandler(
            Func<
                HttpRequestMessage,
                CancellationToken,
                Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}