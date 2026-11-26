using System.Diagnostics;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Search;
using Retromind.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace Retromind.Tests.Services.Search;

public sealed class LibrarySearchPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public async Task LargeLibraryParityAndReproducibleCostMeasurements(int count)
    {
        var roots = CreateLibrary(count);
        foreach (var text in new[] { "Game", "Game 49999", "tag:space AND year:1999", "store:gog AND has:video", "nonexistent" })
        {
            var query = new LibrarySearchQuery(text);
            var expected = LibrarySearchEvaluatorTests.LegacyEvaluate(roots, text);
            var snapshot = LibrarySearchSnapshot.Capture(roots, query: query);
            Assert.Equal(expected.Select(i => i.Id), LibrarySearchEvaluator.Evaluate(snapshot, query).Select(h => h.ItemId));
            // Optional repeated measurements; correctness above remains part of the regular suite.
            if (Environment.GetEnvironmentVariable("RETROMIND_SEARCH_BENCHMARK") != "1") continue;
            var legacy = Measure(() => LibrarySearchEvaluatorTests.LegacyEvaluate(roots, text));
            var capture = Measure(() => LibrarySearchSnapshot.Capture(roots, query: query));
            var evaluate = Measure(() => LibrarySearchEvaluator.Evaluate(snapshot, query));
            var total = Measure(() => LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots, query: query), query));
            output.WriteLine($"{count} | {text} | legacy {legacy} | capture {capture} | evaluate {evaluate} | total {total} | hits {expected.Count}");
            AvaloniaTestDispatcher.EnsureStarted();
            var oldPipeline = await MeasureAsync(() => LegacyPipelineAsync(roots, text));
            var newPipeline = await MeasureAsync(async () =>
            {
                LibrarySearchSnapshot? captured = null;
                await UiThreadHelper.InvokeAsync(() => captured = LibrarySearchSnapshot.Capture(roots, query: query));
                return await LibrarySearchEvaluator.EvaluateAsync(captured!, query);
            });
            output.WriteLine($"{count} | {text} | old pipeline {oldPipeline} | new pipeline {newPipeline}");
        }
        if (Environment.GetEnvironmentVariable("RETROMIND_SEARCH_BENCHMARK") == "1")
        {
            output.WriteLine($"{count} | observer attach/dispose {Measure(() => { using var observer = new LibrarySearchChangeObserver(roots, _ => { }); return true; })}");
            var snapshot = LibrarySearchSnapshot.Capture(roots);
            using var cts = new CancellationTokenSource();
            var visited = 0;
            var watch = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => LibrarySearchEvaluator.Evaluate(snapshot, new("Game"), cts.Token,
                _ => { if (++visited == 100) cts.Cancel(); }));
            output.WriteLine($"{count} | cancel after 100 matches: {watch.Elapsed.TotalMilliseconds:F3} ms, matches {visited}");
        }
    }

    private static async Task<string> MeasureAsync<T>(Func<Task<T>> operation)
    {
        await operation();
        var samples = new List<(double Time, long Bytes)>();
        for (var i = 0; i < 5; i++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            var result = await operation(); watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - allocated));
            GC.KeepAlive(result);
        }
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        return $"{samples[2].Time:F3} ms / {samples[2].Bytes / 1048576.0:F3} MiB";
    }

    // Previous desktop traversal, including one UI handoff per category and live-field sort.
    // Both pipelines omit the unchanged debounce, node counters and result-grid rendering.
    private static Task<List<MediaItem>> LegacyPipelineAsync(List<MediaNode> roots, string text) => Task.Run(async () =>
    {
        var matcher = SearchQueryMatcher.Create(text);
        var result = new List<MediaItem>(256); var seen = new HashSet<string>(StringComparer.Ordinal);
        async Task Visit(MediaNode node)
        {
            List<MediaItem> items = []; List<MediaNode> children = [];
            await UiThreadHelper.InvokeAsync(() => { items = node.Items.ToList(); children = node.Children.ToList(); });
            foreach (var item in items)
                if (matcher.Matches(item) && (string.IsNullOrWhiteSpace(item.Id) || seen.Add(item.Id))) result.Add(item);
            foreach (var child in children) await Visit(child);
        }
        foreach (var root in roots) await Visit(root);
        result.Sort(MediaSortHelper.DisplayOrderComparer);
        return result;
    });

    private static string Measure<T>(Func<T> operation)
    {
        operation(); // Warm JIT and comparer paths, no hardware-dependent assertions.
        var samples = new List<(double Time, long Bytes)>();
        for (var i = 0; i < 5; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var result = operation();
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated));
            GC.KeepAlive(result);
        }
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        var median = samples[2];
        return $"{median.Time:F3} ms / {median.Bytes / 1048576.0:F3} MiB";
    }

    internal static List<MediaNode> CreateLibrary(int count)
    {
        var root = new MediaNode("Library");
        for (var n = 0; n < count / 100; n++)
        {
            var node = new MediaNode($"Category {n}"); root.Children.Add(node);
            // Reverse numeric titles to exercise natural sorting instead of presorted input.
            for (var j = 0; j < 100; j++)
            {
                var id = count - (n * 100 + j) - 1;
                var item = new MediaItem($"Game {id}")
                {
                    Id = $"item-{id}", Description = "A space game with a long description and metadata.",
                    Developer = "Studio", Publisher = "Publisher", Genre = "Action", Platform = "PC",
                    ReleaseDate = new DateTime(1990 + id % 20, 1, 1), IsFavorite = id % 5 == 0,
                    Status = (PlayStatus)(id % 3), MediaType = (MediaType)(id % 3),
                    CustomFields = new() { ["edition"] = "Gold", [CustomFieldKeyHelper.StoreProviderId] = id % 2 == 0 ? "gog" : "steam" }
                };
                item.Tags.Add(id % 3 == 0 ? "space" : "racing");
                item.Assets.Add(new MediaAsset { Type = AssetType.Video, RelativePath = $"Library/{id}/video.mp4" });
                item.Assets.Add(new MediaAsset { Type = AssetType.Cover, RelativePath = $"Library/{id}/cover.webp" });
                node.Items.Add(item);
            }
        }
        return [root];
    }
}
