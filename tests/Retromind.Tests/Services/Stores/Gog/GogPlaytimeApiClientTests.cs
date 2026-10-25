using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Retromind.Services.Stores.Gog.Playtime;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPlaytimeApiClientTests
{
    [Fact]
    public async Task PostSessionAsync_UsesGameplayEndpointBearerTokenAndExpectedPayload()
    {
        const string accessToken = "secret-access-token";
        Uri? requestedUri = null;
        AuthenticationHeaderValue? authorization = null;
        string? contentType = null;
        string? requestBody = null;
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));
        var client = new GogPlaytimeApiClient(httpClient);

        await client.PostSessionAsync(
            "1994983163",
            "46988829925743611",
            accessToken,
            1_792_933_200,
            42);

        Assert.NotNull(requestedUri);
        Assert.Equal("gameplay.gog.com", requestedUri.Host);
        Assert.Equal(
            "/games/1994983163/users/46988829925743611/sessions",
            requestedUri.AbsolutePath);
        Assert.DoesNotContain(accessToken, requestedUri.OriginalString, StringComparison.Ordinal);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal(accessToken, authorization?.Parameter);
        Assert.Equal("application/json", contentType);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal(1_792_933_200, payload.RootElement.GetProperty("session_date").GetInt64());
        Assert.Equal(42, payload.RootElement.GetProperty("time").GetInt32());
    }

    [Fact]
    public async Task PostSessionAsync_PreservesHttpStatusWithoutExposingToken()
    {
        const string accessToken = "do-not-expose";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var client = new GogPlaytimeApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<GogPlaytimeApiException>(() =>
            client.PostSessionAsync(
                "1994983163",
                "46988829925743611",
                accessToken,
                1_792_933_200,
                42));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(accessToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostSessionAsync_RejectsNonNumericIdentifiersWithoutRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var client = new GogPlaytimeApiClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PostSessionAsync(
                "../1994983163",
                "46988829925743611",
                "access-token",
                1_792_933_200,
                42));

        Assert.Equal(0, requestCount);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request);
    }
}
