using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Search;
using Retromind.Tests.Services.Search;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchAreaViewModelSearchLifecycleTests
{
    [Theory]
    [InlineData("query")]
    [InlineData("title")]
    [InlineData("metadata")]
    [InlineData("tags")]
    [InlineData("asset")]
    [InlineData("store_files")]
    [InlineData("store_args")]
    [InlineData("remove")]
    [InlineData("move")]
    [InlineData("protect")]
    [InlineData("lock")]
    [InlineData("scope")]
    [InlineData("dispose")]
    public async Task SupersededEvaluationCannotPublishEvenIfWorkerIgnoresCancellation(string change)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var roots = new ObservableCollection<MediaNode>(LibrarySearchEvaluatorTests.CreateLibrary());
            var item = roots[0].Items[0];
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var evaluations = 0;
            var errors = new RecordingErrorService();
            using var vm = new SearchAreaViewModel(roots, errors, parentalFilterActive: change == "protect")
            {
                EvaluateSearchAsync = async (snapshot, query, token, count) =>
                {
                    if (Interlocked.Increment(ref evaluations) == 1)
                    {
                        var old = LibrarySearchEvaluator.Evaluate(snapshot, query, onMatch: count);
                        captured.TrySetResult();
                        await release.Task;
                        return old; // Deliberately hostile worker: the publication guard must still reject this.
                    }
                    return await LibrarySearchEvaluator.EvaluateAsync(snapshot, query, token, count);
                }
            };
            var published = 0;
            vm.SearchResults.CollectionChanged += (_, _) => { published++; secondPublished.TrySetResult(); };
            vm.SearchText = "id:item-1";
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (change)
            {
                case "query": vm.SearchText = "id:item-2"; break;
                case "title": item.Title = "Renamed"; break;
                case "metadata": item.Description = "Edited"; break;
                case "tags": item.Tags.Add("new"); break;
                case "asset": item.Assets[0].RelativePath = "new.mp4"; break;
                case "store_files": item.Files = [new() { Path = "steam" }]; break;
                case "store_args": item.LauncherArgs = "steam://run/123"; break;
                case "remove": roots[0].Items.Remove(item); break;
                case "move": roots[0].Items.Remove(item); roots[1].Items.Add(item); break;
                case "protect": item.IsProtected = true; break;
                case "lock": item.IsProtected = true; vm.SetParentalFilterActive(true); break;
                case "scope": vm.ApplyScopeSelection([roots[1].Id]); break;
                case "dispose": vm.Dispose(); break;
            }
            // The obsolete worker finishes while its replacement is still in the debounce.
            release.TrySetResult();
            await Task.Delay(80);
            Assert.Empty(vm.SearchResults);
            Assert.Equal(0, published);
            if (change is "query" or "title" or "metadata" or "tags" or "asset" or "store_files" or "store_args" or "move")
            {
                await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(vm.SearchResults);
                Assert.Equal(change == "query" ? "item-2" : "item-1", vm.SearchResults[0].Id);
                if (change == "move") { Assert.Equal(0, roots[0].GlobalSearchHitCount); Assert.Equal(1, roots[1].GlobalSearchHitCount); }
            }
            else if (change != "dispose")
            {
                await Task.Delay(350);
                Assert.Empty(vm.SearchResults);
            }
            Assert.Empty(errors.Reports);
        });
    }

    [Fact]
    public async Task IndependentViewModelsRetainQueriesScopesFiltersSelectionAndCancellation()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var roots = new ObservableCollection<MediaNode>(LibrarySearchEvaluatorTests.CreateLibrary());
            using var first = new SearchAreaViewModel(roots, new RecordingErrorService());
            using var second = new SearchAreaViewModel(roots, new RecordingErrorService());
            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SearchResults.CollectionChanged += (_, _) => firstReady.TrySetResult();
            second.SearchResults.CollectionChanged += (_, _) => secondReady.TrySetResult();
            first.SearchText = "Game"; first.OnlyFavorites = true;
            second.ApplyScopeSelection([roots[0].Children[0].Id]); second.SearchText = "Game";
            await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(5));
            first.SelectedMediaItem = Assert.Single(first.SearchResults);
            second.SelectedMediaItem = second.SearchResults[0];
            Assert.Equal(2, second.SearchResults.Count);
            first.Dispose();
            second.RefreshResults();
            await Task.Delay(400);
            Assert.Equal("Game", second.SearchText);
            Assert.False(second.OnlyFavorites);
            Assert.Equal(2, second.SearchResults.Count);
            Assert.NotNull(second.SelectedMediaItem);
            Assert.Equal([roots[0].Children[0].Id], second.GetSelectedScopeIdsSnapshot());
        });
    }

    [Fact]
    public async Task RapidTypingCapturesOnlyTheFinalRequestAndKeepsScopeCounterSemantics()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var roots = new ObservableCollection<MediaNode>(LibrarySearchEvaluatorTests.CreateLibrary());
            var evaluations = 0;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var vm = new SearchAreaViewModel(roots, new RecordingErrorService())
            {
                EvaluateSearchAsync = (snapshot, query, token, count) =>
                {
                    Interlocked.Increment(ref evaluations);
                    return LibrarySearchEvaluator.EvaluateAsync(snapshot, query, token, count);
                }
            };
            vm.SearchResults.CollectionChanged += (_, _) => ready.TrySetResult();
            vm.ApplyScopeSelection([roots[0].Children[0].Id]);
            foreach (var text in new[] { "G", "Ga", "Gam", "Game" }) vm.SearchText = text;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, evaluations);
            Assert.Equal(2, vm.SearchResults.Count);
            Assert.Equal(0, roots[0].GlobalSearchHitCount);
            Assert.Equal(2, roots[0].Children[0].GlobalSearchHitCount);
        });
    }
}
