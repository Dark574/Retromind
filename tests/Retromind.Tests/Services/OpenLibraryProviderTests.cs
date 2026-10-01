using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class OpenLibraryProviderTests
{
    [Fact]
    public async Task SearchAsync_IdentifiesRetromindWithProjectContact()
    {
        string? userAgent = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"docs\":[]}", Encoding.UTF8, "application/json")
            };
        }));
        var provider = new OpenLibraryProvider(new ScraperConfig(), client);

        await provider.SearchAsync("Dune");

        Assert.Contains("Retromind/1.0", userAgent, StringComparison.Ordinal);
        Assert.Contains("retromind.project@proton.me", userAgent, StringComparison.Ordinal);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
