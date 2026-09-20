using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class EditMediaViewModelPlayStatisticsTests
{
    [Fact]
    public void PlayStatisticsChangesRemainStagedUntilEditorIsSaved()
    {
        using var temp = new TemporaryDirectory();
        var originalLastPlayed = new DateTime(2026, 9, 1, 12, 30, 15, DateTimeKind.Local);
        var item = new MediaItem
        {
            Title = "Test Game",
            PlayCount = 3,
            TotalPlayTime = new TimeSpan(0, 2, 3, 4, 500),
            LastPlayed = originalLastPlayed
        };
        using var viewModel = CreateViewModel(temp, item);

        Assert.Equal(3m, viewModel.PlayCount);
        Assert.Equal(2m, viewModel.PlayTimeHours);
        Assert.Equal(3m, viewModel.PlayTimeMinutes);
        Assert.Equal(4m, viewModel.PlayTimeSeconds);

        viewModel.PlayCount = 7;
        viewModel.PlayTimeHours = 5;
        viewModel.PlayTimeMinutes = 6;
        viewModel.PlayTimeSeconds = 7;
        viewModel.LastPlayedDate = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 20)));
        viewModel.LastPlayedTime = new TimeSpan(8, 9, 10);

        Assert.Equal(3, item.PlayCount);
        Assert.Equal(new TimeSpan(0, 2, 3, 4, 500), item.TotalPlayTime);
        Assert.Equal(originalLastPlayed, item.LastPlayed);

        viewModel.SaveAndCloseCommand.Execute(null);

        Assert.Equal(7, item.PlayCount);
        Assert.Equal(new TimeSpan(5, 6, 7), item.TotalPlayTime);
        Assert.Equal(
            new DateTime(2026, 9, 20, 8, 9, 10, DateTimeKind.Local),
            item.LastPlayed);
    }

    [Fact]
    public void ResetPlayStatisticsClearsAllPlayEvidenceWhenSaved()
    {
        using var temp = new TemporaryDirectory();
        var item = new MediaItem
        {
            Title = "Test Game",
            PlayCount = 3,
            TotalPlayTime = TimeSpan.FromHours(2),
            LastPlayed = DateTime.Now
        };
        using var viewModel = CreateViewModel(temp, item);

        viewModel.ResetPlayStatisticsCommand.Execute(null);

        Assert.Equal(3, item.PlayCount);
        Assert.Equal(TimeSpan.FromHours(2), item.TotalPlayTime);
        Assert.NotNull(item.LastPlayed);

        viewModel.SaveAndCloseCommand.Execute(null);

        Assert.Equal(0, item.PlayCount);
        Assert.Equal(TimeSpan.Zero, item.TotalPlayTime);
        Assert.Null(item.LastPlayed);
    }

    [Fact]
    public void SavingWithoutStatisticsEditsPreservesFullPrecision()
    {
        using var temp = new TemporaryDirectory();
        var totalPlayTime = new TimeSpan(123456789);
        var lastPlayed = new DateTime(2026, 9, 1, 12, 30, 15, 321, DateTimeKind.Utc).AddTicks(4567);
        var item = new MediaItem
        {
            Title = "Test Game",
            PlayCount = 3,
            TotalPlayTime = totalPlayTime,
            LastPlayed = lastPlayed
        };
        using var viewModel = CreateViewModel(temp, item);

        viewModel.SaveAndCloseCommand.Execute(null);

        Assert.Equal(totalPlayTime, item.TotalPlayTime);
        Assert.Equal(lastPlayed, item.LastPlayed);
    }

    private static EditMediaViewModel CreateViewModel(TemporaryDirectory temp, MediaItem item)
    {
        var parent = new MediaNode { Name = "Games" };
        parent.Items.Add(item);
        return new EditMediaViewModel(
            item,
            new AppSettings(),
            new FileManagementService(temp.CreateDirectory("Library")),
            [parent.Name],
            new UnexpectedIdentificationService(),
            rootNodes: new ObservableCollection<MediaNode> { parent },
            parentNode: parent);
    }

    private sealed class UnexpectedIdentificationService : IRetroAchievementsGameIdentificationService
    {
        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");
    }
}
