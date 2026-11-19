using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopContentSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingSearch_CarriesSelectionAndSharedFiltersBackToTheMediaView(bool selectSearchResult)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        var originItem = new MediaItem("New Game 1") { IsFavorite = true };
        var resultItem = new MediaItem("New Game 2") { IsFavorite = true };
        var origin = new MediaNode("Origin") { Items = [originItem] };
        var destination = new MediaNode("Destination") { Items = [resultItem] };
        ObservableCollection<MediaNode> roots = [origin, destination];
        var errors = new RecordingErrorService();
        var session = new DesktopContentSession();
        object? published = null;
        session.ContentChanged += content => published = content;
        Task load = Task.CompletedTask;

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                session.SetMediaFilters("Game", onlyFavorites: true);
                load = session.LoadMediaAsync(origin, roots, false, node => (150, originItem.Id));
            });
            await load.WaitAsync(TestTimeout);

            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                var originalMedia = Assert.IsType<MediaAreaViewModel>(published);
                Assert.Same(originItem, originalMedia.SelectedMediaItem);

                session.OpenSearch(origin.Id, () => new SearchAreaViewModel(roots, errors));
                var search = Assert.IsType<SearchAreaViewModel>(published);
                Assert.Null(session.MediaArea);
                Assert.Equal("Game", search.SearchText);
                Assert.True(search.OnlyFavorites);
                Assert.Null(search.SelectedMediaItem);
                search.SearchResults.Add(originItem);
                Assert.Same(originItem, search.SelectedMediaItem);
                search.SearchText = "New";
                search.ApplyScopeSelection([destination.Id]);
                search.SearchResults.Add(resultItem);
                search.SelectedMediaItem = selectSearchResult ? resultItem : null;

                var (nodeId, selectedId, desiredId) = session.CloseSearch();
                Assert.Equal(origin.Id, nodeId);
                Assert.Equal(selectSearchResult ? resultItem.Id : null, selectedId);
                var expectedItem = selectSearchResult ? resultItem : originItem;
                var target = selectSearchResult ? destination : origin;
                Assert.Equal(expectedItem.Id, desiredId);
                Assert.Null(session.SearchArea);
                session.RememberNodeSelection(target.Id, expectedItem.Id);
                load = session.LoadMediaAsync(target, roots, false, node => (150, null));
            });
            await load.WaitAsync(TestTimeout);

            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                var restored = Assert.IsType<MediaAreaViewModel>(published);
                Assert.Same(selectSearchResult ? resultItem : originItem, restored.SelectedMediaItem);
                Assert.Equal("New", restored.SearchText);
                Assert.True(restored.OnlyFavorites);

                session.OpenSearch(restored.Node.Id, () => new SearchAreaViewModel(roots, errors));
                var reopened = Assert.IsType<SearchAreaViewModel>(published);
                Assert.Equal(destination.Id, Assert.Single(reopened.GetSelectedScopeIdsSnapshot()));
            });
        }
        finally
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ => session.Dispose());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateMediaLoad_CannotOverwriteANewerNodeOrSearch(bool openSearch)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        var oldItem = new MediaItem("Old game");
        oldItem.SetActiveAsset(AssetType.Cover, "current-cover.png");
        var original = new MediaNode("Original") { Items = [oldItem] };
        var next = new MediaNode("Next");
        ObservableCollection<MediaNode> roots = [original, next];
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOldProjection = new TaskCompletionSource<DesktopMediaProjectionBuilder.Projection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new DesktopContentSession((node, _, _, token) =>
        {
            if (!ReferenceEquals(node, original))
                return Task.FromResult(new DesktopMediaProjectionBuilder.Projection([], []));

            started.SetResult(token);
            return finishOldProjection.Task;
        });
        object? published = null;
        session.ContentChanged += content => published = content;
        Task oldLoad = Task.CompletedTask;
        Task newLoad = Task.CompletedTask;

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
                oldLoad = session.LoadMediaAsync(original, roots, false, node => (150, null)));
            var oldToken = await started.Task.WaitAsync(TestTimeout);

            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                if (openSearch)
                    session.OpenSearch(original.Id, () => new SearchAreaViewModel(roots, new RecordingErrorService()));
                else
                    newLoad = session.LoadMediaAsync(next, roots, false, node => (150, null));
            });
            await newLoad.WaitAsync(TestTimeout);
            Assert.True(oldToken.IsCancellationRequested);

            finishOldProjection.SetResult(new([oldItem], [new(oldItem, "stale.png", null, null, null)]));
            await oldLoad.WaitAsync(TestTimeout);

            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Assert.Equal("current-cover.png", oldItem.GetPrimaryAssetPath(AssetType.Cover));
                if (openSearch)
                    Assert.Same(session.SearchArea, Assert.IsType<SearchAreaViewModel>(published));
                else
                    Assert.Equal(next.Id, Assert.IsType<MediaAreaViewModel>(published).Node.Id);
            });
        }
        finally
        {
            finishOldProjection.TrySetCanceled();
            await AvaloniaTestDispatcher.InvokeAsync(_ => session.Dispose());
        }
    }
}
