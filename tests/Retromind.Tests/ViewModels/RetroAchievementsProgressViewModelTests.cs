using Retromind.Models;
using Retromind.Services.RetroAchievements;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class RetroAchievementsProgressViewModelTests
{
    [Fact]
    public async Task SelectItemAsync_HidesProgressForUnidentifiedGame()
    {
        var requestCount = 0;
        using var viewModel = CreateViewModel((gameId, forceRefresh, cancellationToken) =>
        {
            requestCount++;
            return Task.FromResult(CreateSnapshot(gameId));
        });

        await viewModel.SelectItemAsync(new MediaItem { Title = "Unidentified" });

        Assert.False(viewModel.IsVisible);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.Snapshot);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task SelectItemAsync_ShowsAchievementAndHardcoreProgress()
    {
        using var viewModel = CreateViewModel((gameId, forceRefresh, cancellationToken) =>
            Task.FromResult(CreateSnapshot(gameId)));

        await viewModel.SelectItemAsync(CreateIdentifiedItem(gameId: 123));

        Assert.True(viewModel.IsVisible);
        Assert.True(viewModel.HasProgress);
        Assert.False(viewModel.IsLoading);
        Assert.Equal(10, viewModel.ProgressMaximum);
        Assert.Equal(4, viewModel.ProgressValue);
        Assert.Contains("4", viewModel.SummaryText, StringComparison.Ordinal);
        Assert.Contains("2", viewModel.HardcoreSummaryText, StringComparison.Ordinal);
        Assert.False(viewModel.ShowStatus);
    }

    [Fact]
    public async Task SelectItemAsync_LabelsPersistedFallback()
    {
        using var viewModel = CreateViewModel((gameId, forceRefresh, cancellationToken) =>
            Task.FromResult(CreateSnapshot(gameId, usedCachedFallback: true)));

        await viewModel.SelectItemAsync(CreateIdentifiedItem(gameId: 123));

        Assert.True(viewModel.HasProgress);
        Assert.True(viewModel.ShowStatus);
        Assert.NotEmpty(viewModel.StatusText);
    }

    [Fact]
    public async Task RefreshCommand_ForcesServiceRefresh()
    {
        var forceRefreshValues = new List<bool>();
        using var viewModel = CreateViewModel((gameId, forceRefresh, cancellationToken) =>
        {
            forceRefreshValues.Add(forceRefresh);
            return Task.FromResult(CreateSnapshot(gameId));
        });
        await viewModel.SelectItemAsync(CreateIdentifiedItem(gameId: 123));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([false, true], forceRefreshValues);
    }

    [Fact]
    public async Task SelectItemAsync_DoesNotApplyResultFromPreviousSelection()
    {
        var firstResult = new TaskCompletionSource<RetroAchievementsProgressSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<RetroAchievementsProgressSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = CreateViewModel((gameId, forceRefresh, cancellationToken) =>
            gameId == 123 ? firstResult.Task : secondResult.Task);

        var firstLoad = viewModel.SelectItemAsync(CreateIdentifiedItem(gameId: 123));
        var secondLoad = viewModel.SelectItemAsync(CreateIdentifiedItem(gameId: 456));
        secondResult.SetResult(CreateSnapshot(gameId: 456));
        await secondLoad;
        firstResult.SetResult(CreateSnapshot(gameId: 123));
        await firstLoad;

        Assert.Equal(456, viewModel.Snapshot?.Progress.GameId);
    }

    private static RetroAchievementsProgressViewModel CreateViewModel(
        Func<int, bool, CancellationToken, Task<RetroAchievementsProgressSnapshot>> getProgress)
    {
        var settings = new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = true,
                Username = "TestUser"
            }
        };
        return new RetroAchievementsProgressViewModel(
            settings,
            new StubProgressService(getProgress));
    }

    private static MediaItem CreateIdentifiedItem(int gameId) =>
        new()
        {
            Title = "Test Game",
            RetroAchievementsGame = new RetroAchievementsGameIdentity
            {
                GameId = gameId,
                ConsoleId = 4,
                GameSystemId = "gb",
                Hash = "25f9e794323b453885f5181f1b624d0b",
                Title = "Test Game"
            }
        };

    private static RetroAchievementsProgressSnapshot CreateSnapshot(
        int gameId,
        bool usedCachedFallback = false)
    {
        return new RetroAchievementsProgressSnapshot(
            new RetroAchievementsGameProgress
            {
                GameId = gameId,
                Title = "Test Game",
                ConsoleId = 4,
                AchievementCount = 10,
                AwardedCount = 4,
                AwardedHardcoreCount = 2,
                CompletionPercent = 40,
                CompletionHardcorePercent = 20
            },
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero),
            usedCachedFallback);
    }

    private sealed class StubProgressService(
        Func<int, bool, CancellationToken, Task<RetroAchievementsProgressSnapshot>> getProgress)
        : IRetroAchievementsProgressService
    {
        public Task<RetroAchievementsProgressSnapshot> GetProgressAsync(
            int gameId,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            return getProgress(gameId, forceRefresh, cancellationToken);
        }

        public void Invalidate(int gameId)
        {
        }

        public void ClearMemoryCache()
        {
        }
    }
}
