using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Services.Search;
using Retromind.Tests.Services.Search;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Xunit.Abstractions;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeSearchPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public async Task SnapshotPublicationAndRapidInputHaveBoundedWork(int count)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var roots = LibrarySearchPerformanceTests.CreateLibrary(count);
            var clock = new ManualSearchTimeProvider();
            var calls = 0;
            using var context = new BigModeSearchViewModel(roots, roots[0], true)
            {
                SearchTimeProvider = clock,
                EvaluateAsync = (snapshot, query, token) => { calls++; return LibrarySearchEvaluator.EvaluateAsync(snapshot, query, token); }
            };
            foreach (var text in new[] { "G", "Ga", "Gam", "Game", "Game 4", "Game 49" }) context.SearchText = text;
            clock.Advance(249); Assert.Equal(0, calls);
            clock.Advance(1); await context.PendingSearch; Assert.Equal(1, calls);
            Assert.All(context.Results, row => Assert.False(row.Item.IsProtected));
            context.FocusResults();
            for (var i = 0; i < 1000; i++) context.Navigate(GamepadService.GamepadDirection.Down);
            Assert.Equal(1, calls); // Selection does not evaluate, capture or load media.
            if (Environment.GetEnvironmentVariable("RETROMIND_SEARCH_BENCHMARK") != "1") return;

            context.Dispose();
            using var publishingContext = new BigModeSearchViewModel(roots, roots[0], true);
            // Measure the same production ApplyResults path without adding runtime instrumentation.
            var publish = typeof(BigModeSearchViewModel).GetMethod("ApplyResults", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<IReadOnlyList<LibrarySearchHit>>>(publishingContext);
            for (var sample = 0; sample < 4; sample++)
            {
                LibrarySearchChangeObserver? observer = null;
                var attach = Measure(() => observer = new LibrarySearchChangeObserver(roots, _ => { }));
                var detach = Measure(() => observer!.Dispose());
                output.WriteLine($"{count} observer sample {sample} | attach {attach.Time:F3} ms | detach {detach.Time:F3} ms | {attach.Bytes / 1048576.0:F3} MiB allocated; no overlapping search observer");
            }
            foreach (var text in new[] { "Game", $"\"Game {count-1}\"", "tag:space AND year:1999", "store:gog AND has:video", "nonexistent" })
            {
                var query = new LibrarySearchQuery(text);
                LibrarySearchSnapshot? snapshot = null;
                IReadOnlyList<LibrarySearchHit> hits = [];
                var captures = new List<(double Time,long Bytes)>();
                var workers = new List<(double Time,long Bytes)>();
                var publications = new List<(double Time,long Bytes)>();
                for (var i = 0; i < 7; i++)
                {
                    var capture = Measure(() => snapshot = LibrarySearchSnapshot.Capture(roots, parentalFilterActive: true,
                        isNodeVisible: new BigModeVisibilityPolicy(true).IsNodeVisible, query: query));
                    var worker = await Task.Run(() => Measure(() => hits = LibrarySearchEvaluator.Evaluate(snapshot!, query)));
                    var publication = Measure(() => publish(hits));
                    if (i == 0) continue; // Warm JIT, constructors and capacities.
                    captures.Add(capture); workers.Add(worker); publications.Add(publication);
                }
                double Median(List<(double Time,long Bytes)> values) => values.OrderBy(x => x.Time).ElementAt(3).Time;
                output.WriteLine($"{count} | {text} | {hits.Count} hits | capture median/max {Median(captures):F3}/{captures.Max(x => x.Time):F3} ms | worker {Median(workers):F3}/{workers.Max(x => x.Time):F3} ms | publish {Median(publications):F3}/{publications.Max(x => x.Time):F3} ms | allocated capture/worker/publish {captures[0].Bytes / 1048576.0:F3}/{workers[0].Bytes / 1048576.0:F3}/{publications[0].Bytes / 1048576.0:F3} MiB");
            }
            output.WriteLine("Managed dispatcher, in-memory models, no window/rendering, SDL or VLC; capture excludes observer attachment, publication includes result row construction and notifications but no rendered controls. Debounce is controlled, not timed.");
        });
    }

    [Fact]
    public async Task ClosedContextsRowsAndSnapshotsAreCollectibleWhileLibraryRemainsAlive()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        var roots = LibrarySearchPerformanceTests.CreateLibrary(5_000);
        var baseline = GC.GetTotalMemory(true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocated = GC.GetTotalAllocatedBytes(true);
        var weak = new List<WeakReference>();
        for (var i = 0; i < 12; i++) weak.AddRange(await ClosedContext(roots));
        // Let async frames and the dispatcher's last operation leave their stacks.
        await UiThreadHelper.InvokeAsync(() => { }); await Task.Delay(20);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(weak, reference => Assert.False(reference.IsAlive));
        output.WriteLine($"12 searches / 5000 items: allocated {(GC.GetTotalAllocatedBytes(true)-allocated)/1048576.0:F3} MiB, retained delta {(GC.GetTotalMemory(true)-baseline)/1048576.0:F3} MiB; GC {GC.CollectionCount(0)-collections[0]}/{GC.CollectionCount(1)-collections[1]}/{GC.CollectionCount(2)-collections[2]}. Explicit collection included; no preview/window.");
        GC.KeepAlive(roots);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ClosedContext(List<MediaNode> roots)
    {
        WeakReference[] references = [];
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var clock = new ManualSearchTimeProvider();
            using var context = new BigModeSearchViewModel(roots, null, false)
            {
                SearchTimeProvider = clock,
                EvaluateAsync = (snapshot, query, token) =>
                {
                    references = [new(snapshot)];
                    return LibrarySearchEvaluator.EvaluateAsync(snapshot, query, token);
                }
            };
            context.SearchText = "Game"; clock.Advance(250); await context.PendingSearch;
            references = [references[0], new(context), new(context.Results[0])];
        });
        return references;
    }
    private static (double Time,long Bytes) Measure(Action action)
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew(); action(); watch.Stop();
        return (watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread()-bytes);
    }
}
