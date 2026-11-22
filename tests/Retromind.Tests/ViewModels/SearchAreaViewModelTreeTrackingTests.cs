using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchAreaViewModelTreeTrackingTests
{
    [Fact]
    public async Task UnplayedFilter_RefreshesAfterTheFirstLaunchAndKeepsTheOtherSelection()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var played = new MediaItem("Started");
            var selected = new MediaItem("Unplayed");
            var root = new MediaNode("Games");
            root.Items.Add(played);
            root.Items.Add(selected);
            var roots = new ObservableCollection<MediaNode> { root };
            const string query = "played:false";
            using var search = new SearchAreaViewModel(roots, new RecordingErrorService());
            using var category = new MediaAreaViewModel(root, 150, initialSearchText: query);
            var notifications = 0;
            var tracker = new LibraryChangeTracker(new MediaDataService(), (_, args) =>
            {
                if (args.PropertyName is { } property && SearchQueryMatcher.IsLiveItemFilterInput(property))
                {
                    notifications++;
                    search.RefreshAfterItemChange(property);
                }
            });
            tracker.Initialize(roots);
            try
            {
                var initial = WaitForItems(search.SearchResults, () => search.SearchResults.Count == 2);
                search.SearchText = query;
                await initial;
                search.SelectedMediaItem = selected;
                category.SelectedMediaItem = selected;
                var nextSearch = WaitForItems(search.SearchResults, () => search.SearchResults.Count == 1);
                var nextCategory = WaitForItems(category.FilteredItems, () => category.FilteredItems.Count == 1);

                // The launcher updates all three play statistics together.
                played.LastPlayed = new DateTime(2026, 11, 22);
                played.PlayCount = 1;
                played.TotalPlayTime = TimeSpan.FromMinutes(1);
                await Task.WhenAll(nextSearch, nextCategory);

                Assert.Equal(1, notifications);
                Assert.Equal(1, root.GlobalSearchHitCount);
                Assert.Same(selected, Assert.Single(search.SearchResults));
                Assert.Same(selected, Assert.Single(category.FilteredItems));
                Assert.Same(selected, search.SelectedMediaItem);
                Assert.Same(selected, category.SelectedMediaItem);
            }
            finally { tracker.StopTracking(); }
        });
    }

    [Theory]
    [InlineData("title:Game", nameof(MediaItem.HasPlayedForSearch), false, false, false)]
    [InlineData("played:false", nameof(MediaItem.HasPlayedForSearch), false, false, true)]
    [InlineData("fav:true", nameof(MediaItem.IsFavorite), false, false, true)]
    [InlineData("", nameof(MediaItem.IsFavorite), true, false, true)]
    [InlineData("", nameof(MediaItem.Status), false, true, true)]
    public void LiveSearchInputs_OnlyInvalidateFiltersThatUseThem(
        string query, string property, bool onlyFavorites, bool statusFilter, bool expected) =>
        Assert.Equal(expected, SearchQueryMatcher.Create(query)
            .IsAffectedByItemChange(property, onlyFavorites, statusFilter));

    [Fact]
    public async Task GogUpdateFilter_RefreshesBothViewsAndCountsWithoutLosingAnotherSelection()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var first = CreateGogItem("First", "123");
            var selected = CreateGogItem("Selected", "456");
            var root = new MediaNode("Games");
            root.Items.Add(first);
            root.Items.Add(selected);
            var roots = new ObservableCollection<MediaNode> { root };
            const string query = "store:GOG AND (has:gogupdate OR gogupdate:true)";
            using var search = new SearchAreaViewModel(roots, new RecordingErrorService());
            using var category = new MediaAreaViewModel(root, 150, initialSearchText: query);
            var notifications = 0;
            var tracker = new LibraryChangeTracker(new MediaDataService(), (_, args) =>
            {
                if (args.PropertyName == nameof(MediaItem.GogUpdateSearchState))
                {
                    notifications++;
                    search.RefreshAfterItemChange(args.PropertyName);
                }
            });
            tracker.Initialize(roots);
            try
            {
                var initialSearch = WaitForItems(search.SearchResults, () => search.SearchResults.Count == 2);
                search.SearchText = query;
                await initialSearch;
                search.SelectedMediaItem = selected;
                category.SelectedMediaItem = selected;
                Assert.Equal(2, root.GlobalSearchHitCount);

                // A new check timestamp and a switch from base-game to DLC availability
                // leave the search-facing state unchanged.
                first.CustomFields = new(first.CustomFields)
                {
                    [CustomFieldKeyHelper.StoreUpdateLastCheckedUtc] = "time-2",
                    [CustomFieldKeyHelper.StoreUpdateAvailable] = "false",
                    [CustomFieldKeyHelper.StoreDlcUpdateAvailable] = "true"
                };
                Assert.Equal(0, notifications);

                var nextSearch = WaitForItems(search.SearchResults, () => search.SearchResults.Count == 1);
                var nextCategory = WaitForItems(category.FilteredItems, () => category.FilteredItems.Count == 1);
                // Make two transitions while the previous search is waiting on the same debounce.
                first.CustomFields = new(first.CustomFields) { [CustomFieldKeyHelper.StoreDlcUpdateAvailable] = "false" };
                first.CustomFields = new(first.CustomFields) { [CustomFieldKeyHelper.StoreDlcUpdateAvailable] = "true" };
                first.CustomFields = new(first.CustomFields) { [CustomFieldKeyHelper.StoreDlcUpdateAvailable] = "false" };
                await Task.WhenAll(nextSearch, nextCategory);

                Assert.Equal(3, notifications);
                Assert.Equal(1, root.GlobalSearchHitCount);
                Assert.Same(selected, Assert.Single(search.SearchResults));
                Assert.Same(selected, Assert.Single(category.FilteredItems));
                Assert.Same(selected, search.SelectedMediaItem);
                Assert.Same(selected, category.SelectedMediaItem);
            }
            finally { tracker.StopTracking(); }
        });
    }

    [Theory]
    [InlineData("store:GOG", false)]
    [InlineData("title:\"has:gogupdate\"", false)]
    [InlineData("NOT (title:Fixture OR missing:gogupdate)", true)]
    public void GogUpdateFilter_OnlyTracksQueriesThatUseTheField(string query, bool expected) =>
        Assert.Equal(expected, SearchQueryMatcher.Create(query).UsesGogUpdate);

    private static MediaItem CreateGogItem(string name, string id) => new(name)
    {
        CustomFields = new()
        {
            [CustomFieldKeyHelper.StoreProviderId] = "gog",
            [CustomFieldKeyHelper.StoreGameId] = id,
            [CustomFieldKeyHelper.StoreUpdateAvailable] = "true"
        }
    };

    private static async Task WaitForItems(INotifyCollectionChanged collection, Func<bool> matches)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (matches())
                applied.TrySetResult();
        }
        collection.CollectionChanged += OnChanged;
        try { await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { collection.CollectionChanged -= OnChanged; }
    }

    [Fact]
    public void ScopeDialogFactory_PassesTheExistingErrorServiceAndDoesNotChangeCurrentSelection()
    {
        var root = new MediaNode("Root");
        var child = new MediaNode("Child");
        var sibling = new MediaNode("Sibling");
        root.Children.Add(child);
        root.Children.Add(sibling);
        var errors = new RecordingErrorService();
        using var vm = new SearchAreaViewModel(new[] { root }, errors);
        vm.ApplyScopeSelection([child.Id]);
        var dialog = vm.CreateScopeDialogViewModel();
        Assert.Null(dialog.RootNodes[0].IsChecked);
        dialog.ClearAllCommand.Execute(null);
        Assert.True(vm.GetSelectedScopeIdsSnapshot().SetEquals([child.Id]));
        Assert.Empty(errors.Reports);

        // The dialog must reject a hierarchy that became invalid after the search area was created.
        // An ordinary list avoids unrelated live search/tree tracking during this controlled mutation.
        child.Children.Add(root);
        var invalid = vm.CreateScopeDialogViewModel();
        Assert.True(invalid.HasBuildFailure);
        Assert.True(invalid.GetSelectedNodeIds().SetEquals([child.Id]));
        Assert.Equal("SearchScope.BuildFailed", Assert.Single(errors.Reports).Code);
        Assert.True(vm.GetSelectedScopeIdsSnapshot().SetEquals([child.Id]));
        child.Children.Remove(root);
    }

    [Fact]
    public void ScopeDialogFactory_KeepsTheExistingParentalRootFilter()
    {
        var visible = new MediaNode("Visible") { IsVisibleInTree = true };
        var hidden = new MediaNode("Hidden") { IsVisibleInTree = false };
        using var vm = new SearchAreaViewModel(new[] { visible, hidden }, new RecordingErrorService(), parentalFilterActive: true);
        var dialog = vm.CreateScopeDialogViewModel();
        Assert.Same(visible, Assert.Single(dialog.RootNodes).Node);
    }

    [Fact]
    public async Task RemovingDeeplyNestedNode_RemovesItFromExplicitSearchScope() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Root");
            var child = new MediaNode("Child");
            var grandchild = new MediaNode("Grandchild");
            root.Children.Add(child);
            child.Children.Add(grandchild);

            var roots = new ObservableCollection<MediaNode> { root };
            using var viewModel = new SearchAreaViewModel(roots, new RecordingErrorService());
            viewModel.ApplyScopeSelection([grandchild.Id]);

            Assert.Equal(1, viewModel.SelectedScopeCount);

            child.Children.Remove(grandchild);

            Assert.Equal(0, viewModel.SelectedScopeCount);
        });

    [Fact]
    public async Task NewlyAddedNestedSubtree_IsObservedRecursively() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Root");
            var child = new MediaNode("Child");
            root.Children.Add(child);

            var roots = new ObservableCollection<MediaNode> { root };
            using var viewModel = new SearchAreaViewModel(roots, new RecordingErrorService());

            var grandchild = new MediaNode("Grandchild");
            var greatGrandchild = new MediaNode("Great-grandchild");
            grandchild.Children.Add(greatGrandchild);
            child.Children.Add(grandchild);
            viewModel.ApplyScopeSelection([greatGrandchild.Id]);

            Assert.Equal(1, viewModel.SelectedScopeCount);

            grandchild.Children.Remove(greatGrandchild);

            Assert.Equal(0, viewModel.SelectedScopeCount);
        });
}
