using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Retromind.Services.Stores.Gog.Achievements;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogAchievementsApiClientTests
{
    [Fact]
    public async Task GetGameProgressAsync_ParsesProgressAndUsesBearerHeader()
    {
        const string accessToken = "secret-access-token";
        Uri? requestedUri = null;
        AuthenticationHeaderValue? authorization = null;
        string? language = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            language = request.Headers.TryGetValues("X-Gog-Lc", out var values)
                ? Assert.Single(values)
                : null;
            return JsonResponse(
                """
                {
                  "items": [
                    {
                      "achievement_id": "101",
                      "achievement_key": "FIRST",
                      "name": "First achievement",
                      "description": "Complete the first task.",
                      "image_url_locked": "https://images.gog.com/locked.png",
                      "image_url_unlocked": "https://images.gog.com/unlocked.png",
                      "visible": true,
                      "date_unlocked": "2026-10-19T05:17:20+00:00",
                      "rarity": 58.1,
                      "rarity_level_description": "Common"
                    },
                    {
                      "achievement_id": 102,
                      "achievement_key": "SECRET",
                      "name": "Secret title",
                      "description": "Secret description",
                      "image_url_locked": "https://images.gog.com/secret-locked.png",
                      "image_url_unlocked": "https://images.gog.com/secret-unlocked.png",
                      "visible": false,
                      "date_unlocked": null,
                      "rarity": 2.4,
                      "rarity_level_description": "Epic"
                    }
                  ],
                  "achievements_mode": "all_visible"
                }
                """);
        }));
        var client = new GogAchievementsApiClient(httpClient);

        var progress = await client.GetGameProgressAsync(
            "1994983163",
            "46988829925743611",
            accessToken,
            "de-DE");

        Assert.Equal("1994983163", progress.GameId);
        Assert.Equal(2, progress.AchievementCount);
        Assert.Equal(1, progress.UnlockedCount);
        Assert.Equal(50, progress.CompletionPercent);
        Assert.Equal(1, progress.LockedHiddenAchievementCount);

        var unlocked = Assert.Single(
            progress.Achievements,
            achievement => achievement.AchievementId == "101");
        Assert.Equal("FIRST", unlocked.Key);
        Assert.Equal("First achievement", unlocked.Title);
        Assert.True(unlocked.IsUnlocked);
        Assert.False(unlocked.IsHidden);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 19, 5, 17, 20, TimeSpan.Zero),
            unlocked.UnlockedAtUtc);
        Assert.Equal("https://images.gog.com/unlocked.png", unlocked.ImageUrl);
        Assert.Equal(58.1, unlocked.Rarity);
        Assert.Equal("Common", unlocked.RarityLevelDescription);

        var hidden = Assert.Single(
            progress.Achievements,
            achievement => achievement.AchievementId == "102");
        Assert.True(hidden.IsHidden);
        Assert.False(hidden.IsUnlocked);
        Assert.Empty(hidden.Title);
        Assert.Empty(hidden.Description);
        Assert.Null(hidden.ImageUrl);

        Assert.NotNull(requestedUri);
        Assert.Equal("gameplay.gog.com", requestedUri.Host);
        Assert.Equal(
            "/clients/1994983163/users/46988829925743611/achievements",
            requestedUri.AbsolutePath);
        Assert.DoesNotContain(accessToken, requestedUri.OriginalString, StringComparison.Ordinal);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal(accessToken, authorization?.Parameter);
        Assert.Equal("de-DE", language);
    }

    [Fact]
    public async Task GetGameProgressAsync_PreservesHttpStatusWithoutExposingToken()
    {
        const string accessToken = "do-not-expose";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = new GogAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<GogAchievementsApiException>(() =>
            client.GetGameProgressAsync(
                "1994983163",
                "46988829925743611",
                accessToken));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(accessToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameProgressAsync_RejectsOversizedResponseBeforeReading()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            var response = JsonResponse("{}");
            response.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1;
            return response;
        }));
        var client = new GogAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<GogAchievementsApiException>(() =>
            client.GetGameProgressAsync(
                "1994983163",
                "46988829925743611",
                "access-token"));

        Assert.Contains("allowed size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameProgressAsync_RejectsOversizedActualStream()
    {
        var payload = new byte[4 * 1024 * 1024 + 1];
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableMemoryStream(payload))
            }));
        var client = new GogAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<GogAchievementsApiException>(() =>
            client.GetGameProgressAsync(
                "1994983163",
                "46988829925743611",
                "access-token"));

        Assert.Contains("allowed size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameProgressAsync_RejectsNonNumericIdentifiersWithoutRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse("{}");
        }));
        var client = new GogAchievementsApiClient(httpClient);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetGameProgressAsync(
                "../1994983163",
                "46988829925743611",
                "access-token"));

        Assert.Equal(0, requestCount);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class NonSeekableMemoryStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
