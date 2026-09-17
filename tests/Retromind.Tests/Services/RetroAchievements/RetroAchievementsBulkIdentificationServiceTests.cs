using Retromind.Models;
using Retromind.Services.RetroAchievements;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsBulkIdentificationServiceTests
{
    [Fact]
    public async Task IdentifyAsync_IdentifiesMatchesAndContinuesPastSkipsAndFailures()
    {
        var calls = new List<string>();
        var singleService = new StubIdentificationService((systemId, filePath, cancellationToken) =>
        {
            calls.Add(filePath);
            return filePath switch
            {
                "/games/match.chd" => Task.FromResult(CreateResult(gameId: 42, title: "Matched Game")),
                "/games/no-match.chd" => Task.FromResult(CreateResult()),
                _ => throw new RetroAchievementsHashException("Unsupported image")
            };
        });
        var service = new RetroAchievementsBulkIdentificationService(singleService);
        var alreadyIdentified = new MediaItem("Existing")
        {
            RetroAchievementsGame = new RetroAchievementsGameIdentity { GameId = 7 }
        };
        var candidates = new[]
        {
            new RetroAchievementsBulkIdentificationCandidate(
                new MediaItem("Match"), "ps1", "/games/match.chd"),
            new RetroAchievementsBulkIdentificationCandidate(
                new MediaItem("No match"), "ps1", "/games/no-match.chd"),
            new RetroAchievementsBulkIdentificationCandidate(
                alreadyIdentified, "ps1", "/games/existing.chd"),
            new RetroAchievementsBulkIdentificationCandidate(
                new MediaItem("No system"), null, "/games/no-system.chd"),
            new RetroAchievementsBulkIdentificationCandidate(
                new MediaItem("No file"), "ps1", null),
            new RetroAchievementsBulkIdentificationCandidate(
                new MediaItem("Failure"), "ps1", "/games/failure.chd")
        };

        var result = await service.IdentifyAsync(candidates, "batch-key");

        Assert.False(result.IsCancelled);
        Assert.Equal(1, result.IdentifiedCount);
        Assert.Equal(1, result.NoMatchCount);
        Assert.Equal(3, result.SkippedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(6, result.Items.Count);
        Assert.Equal(3, calls.Count);
        Assert.All(singleService.ApiKeys, apiKey => Assert.Equal("batch-key", apiKey));

        var match = Assert.Single(result.Items, item =>
            item.Outcome == RetroAchievementsBulkIdentificationOutcome.Identified);
        Assert.Equal(42, match.Identity?.GameId);
        Assert.Equal("Matched Game", match.Identity?.Title);
        Assert.Null(match.Item.RetroAchievementsGame);
    }

    [Fact]
    public async Task IdentifyAsync_ReturnsCompletedResultsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var firstItem = new MediaItem("First");
        var secondItem = new MediaItem("Second");
        var singleService = new StubIdentificationService((systemId, filePath, cancellationToken) =>
        {
            cancellation.Cancel();
            return Task.FromResult(CreateResult(gameId: 42, title: "Matched Game"));
        });
        var service = new RetroAchievementsBulkIdentificationService(singleService);

        var result = await service.IdentifyAsync(
            [
                new RetroAchievementsBulkIdentificationCandidate(firstItem, "ps1", "/games/first.chd"),
                new RetroAchievementsBulkIdentificationCandidate(secondItem, "ps1", "/games/second.chd")
            ],
            "batch-key",
            cancellationToken: cancellation.Token);

        Assert.True(result.IsCancelled);
        var completed = Assert.Single(result.Items);
        Assert.Same(firstItem, completed.Item);
        Assert.Equal(RetroAchievementsBulkIdentificationOutcome.Identified, completed.Outcome);
    }

    private static RetroAchievementsIdentificationResult CreateResult(
        int? gameId = null,
        string title = "")
    {
        var gameHash = new RetroAchievementsGameHash("ps1", 12, "hash");
        var game = gameId.HasValue
            ? new RetroAchievementsGameCatalogEntry
            {
                GameId = gameId.Value,
                Title = title,
                ConsoleId = 12,
                Hashes = ["hash"]
            }
            : null;
        return new RetroAchievementsIdentificationResult(gameHash, game);
    }

    private sealed class StubIdentificationService(
        Func<string, string, CancellationToken, Task<RetroAchievementsIdentificationResult>> identify)
        : IRetroAchievementsGameIdentificationService
    {
        public List<string> ApiKeys { get; } = new();

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Bulk identification must provide its prepared API key.");

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken = default)
        {
            ApiKeys.Add(apiKey);
            return identify(gameSystemId, filePath, cancellationToken);
        }
    }
}
