using System.Net;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Globalization;
using Retromind.Services.Scrapers;
using Retromind.Models;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BulkScrapeViewModelTests
{
    [Fact]
    public void Constructor_IncludesScreenScraperInProviderAwareBulkPipeline()
    {
        var settings = new AppSettings();
        settings.Scrapers.Add(new ScraperConfig { Type = ScraperType.ScreenScraper });
        using var client = new HttpClient(new RecordingHttpHandler());
        var service = new MetadataService(settings, client);
        using var viewModel = new BulkScrapeViewModel(
            new MediaNode("Games"), settings, service.GetProviderAsync, action => action(), action => action());

        Assert.Single(viewModel.AvailableScrapers);
        Assert.Equal(ScraperType.ScreenScraper, viewModel.AvailableScrapers[0].Type);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task StartCommand_AfterBackup_ConnectsOnlyWhileDialogIsOpen(
        bool closeDuringBackup,
        int expectedRequests)
    {
        using var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        var settings = new AppSettings();
        settings.Scrapers.Add(new ScraperConfig
        {
            Type = ScraperType.IGDB,
            ClientId = "test-client",
            ClientSecret = "test-secret"
        });
        var service = new MetadataService(settings, client);
        using var viewModel = new BulkScrapeViewModel(
            new MediaNode("Games"), settings, service.GetProviderAsync, action => action(), action => action());
        var backupCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.OnBeforeStartAsync = () => backupCompleted.Task;

        var start = viewModel.StartCommand.ExecuteAsync(null);
        Assert.False(start.IsCompleted);
        Assert.Equal(0, handler.RequestCount);

        // Closing BulkScrapeView disposes its view model while the backup is pending.
        if (closeDuringBackup)
            viewModel.Dispose();

        backupCompleted.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expectedRequests, handler.RequestCount);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Run_AllImportsSucceedAndCallbackIsAwaited()
    {
        var provider = new StubProvider();
        using var vm = CreateVm(Games(5), provider);
        var imported = 0;
        vm.OnItemScrapedAsync = async (item, result) =>
        {
            await Task.Yield();
            item.Description = result.Title;
            Interlocked.Increment(ref imported);
            return BulkScrapeImportOutcome.Imported;
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(5, imported);
        Assert.Equal(BulkScrapeRunStatus.Completed, vm.RunStatus);
        AssertSummary(vm, total: 5, imported: 5);
        Assert.Equal(100, vm.ProgressValue);
        Assert.False(vm.IsBusy);
        Assert.Equal(5, provider.SearchCount);
        Assert.Contains("[IMPORTED]", vm.LogText);
    }

    [Fact]
    public async Task Run_MissingUnsafeAndAmbiguousMatchesAreNotTechnicalFailures()
    {
        var provider = new StubProvider((query, _) => Task.FromResult(query switch
        {
            "Game 1" => Matches(query),
            "Game 2" => new List<ScraperSearchResult>(),
            "Game 3" => new List<ScraperSearchResult> { new() { Title = "Entirely different title" } },
            _ => new List<ScraperSearchResult> { new() { Id = "a", Title = query }, new() { Id = "b", Title = query } }
        }));
        using var vm = CreateVm(Games(4), provider);
        var calls = 0;
        vm.OnItemScrapedAsync = (_, _) => { calls++; return Task.FromResult(BulkScrapeImportOutcome.Imported); };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
        AssertSummary(vm, 4, imported: 1, noMatch: 3);
        Assert.Equal(BulkScrapeRunStatus.Completed, vm.RunStatus);
        Assert.Contains("[AMBIGUOUS]", vm.LogText);
        Assert.Contains("[MISS]", vm.LogText);
    }

    [Fact]
    public async Task Run_ItemSearchFailureContinuesAndChangesCompletionStatus()
    {
        var provider = new StubProvider((query, _) => query == "Game 2"
            ? throw new HttpRequestException("Fixture search failed") : Task.FromResult(Matches(query)));
        using var vm = CreateVm(Games(3), provider, ScraperType.ScreenScraper);

        await vm.StartCommand.ExecuteAsync(null);

        AssertSummary(vm, 3, imported: 2, failed: 1);
        Assert.Equal(BulkScrapeRunStatus.CompletedWithErrors, vm.RunStatus);
        Assert.False(vm.IsBusy);
        Assert.Contains("[ERROR] Game 2", vm.LogText);
        Assert.Equal(3, provider.SearchCount);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("failed")]
    [InlineData("missing")]
    public async Task Run_CallbackFailuresNeverCountMatchesAsImports(string failure)
    {
        using var vm = CreateVm(Games(2), new StubProvider());
        vm.OnItemScrapedAsync = failure switch
        {
            "missing" => null,
            "failed" => (_, _) => Task.FromResult(BulkScrapeImportOutcome.Failed),
            _ => (_, _) => throw new IOException("Fixture import failed")
        };

        await vm.StartCommand.ExecuteAsync(null);

        AssertSummary(vm, 2, failed: 2);
        Assert.Equal(BulkScrapeRunStatus.CompletedWithErrors, vm.RunStatus);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Run_CallbackSkipIsASeparateNonErrorCategory()
    {
        using var vm = CreateVm(Games(3), new StubProvider());
        vm.OnItemScrapedAsync = (_, _) => Task.FromResult(BulkScrapeImportOutcome.Skipped);
        await vm.StartCommand.ExecuteAsync(null);
        AssertSummary(vm, 3, skipped: 3);
        Assert.Equal(BulkScrapeRunStatus.Completed, vm.RunStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_ProviderConnectionFailureLeavesKnownItemsUnprocessed(bool throwException)
    {
        var connections = 0;
        using var vm = CreateVm(Games(3), new StubProvider(), getProvider: (_, _) =>
        {
            connections++;
            if (throwException) throw new HttpRequestException("Fixture connection failed");
            return Task.FromResult<IMetadataProvider?>(null);
        });

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, connections);
        AssertSummary(vm, 3, notProcessed: 3);
        Assert.Equal(BulkScrapeRunStatus.Failed, vm.RunStatus);
        Assert.Contains("Critical Error (Connecting to provider)", vm.LogText);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Fact]
    public async Task Run_CollectionFailureKeepsPartialCountAndAlwaysCleansUp()
    {
        var root = Games(2);
        root.Children.Add(new MediaNode("Broken") { Items = null! });
        var connections = 0;
        using var vm = CreateVm(root, new StubProvider(), getProvider: (_, _) =>
        {
            connections++;
            return Task.FromResult<IMetadataProvider?>(null);
        });

        await vm.StartCommand.ExecuteAsync(null);

        AssertSummary(vm, 2, notProcessed: 2, collectionComplete: false);
        Assert.Equal(BulkScrapeRunStatus.Failed, vm.RunStatus);
        Assert.Equal(0, connections);
        Assert.Contains("Critical Error (Collecting items)", vm.LogText);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_BackupDeclineOrExceptionDoesNotLeaveBusyState(bool throws)
    {
        using var vm = CreateVm(Games(2), new StubProvider());
        vm.OnBeforeStartAsync = () => throws ? throw new IOException("Fixture backup failure") : Task.FromResult(false);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(throws ? BulkScrapeRunStatus.Failed : BulkScrapeRunStatus.NotStarted, vm.RunStatus);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Fact]
    public async Task Run_UserCancellationKeepsIncompleteWorkOutOfCompletedCount()
    {
        var entered = NewSignal();
        var provider = new StubProvider(async (query, token) =>
        {
            if (query == "Game 2")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Matches(query);
        });
        using var vm = CreateVm(Games(3), provider, ScraperType.ScreenScraper);
        var running = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BulkScrapeRunStatus.Cancelled, vm.RunStatus);
        AssertSummary(vm, 3, imported: 1, notProcessed: 2);
        Assert.Equal(100d / 3, vm.ProgressValue, precision: 5);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Fact]
    public async Task Run_CancellationDuringCallbackStillCountsSuccessfullyFinishedImport()
    {
        using var vm = CreateVm(Games(3), new StubProvider(), ScraperType.ScreenScraper);
        var entered = NewSignal();
        var finish = NewSignal();
        vm.OnItemScrapedAsync = async (_, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
            return BulkScrapeImportOutcome.Imported;
        };
        var running = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelCommand.Execute(null);
        finish.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        AssertSummary(vm, 3, imported: 1, notProcessed: 2);
        Assert.Equal(BulkScrapeRunStatus.Cancelled, vm.RunStatus);
    }

    [Fact]
    public async Task Run_QuotaAbortIsDistinctAndDoesNotInventAnItemFailure()
    {
        var provider = new StubProvider((query, _) => query == "Game 2"
            ? throw new MetadataQuotaExceededException("Fixture quota exhausted") : Task.FromResult(Matches(query)));
        using var vm = CreateVm(Games(3), provider, ScraperType.TheGamesDB);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(BulkScrapeRunStatus.ProviderLimitReached, vm.RunStatus);
        AssertSummary(vm, 3, imported: 1, notProcessed: 2);
        Assert.Equal(2, provider.SearchCount);
        Assert.Contains("[LIMIT]", vm.LogText);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Fact]
    public async Task Run_ParallelProcessingKeepsExactExclusiveCountsAndExistingConcurrency()
    {
        var firstFour = NewSignal();
        var release = NewSignal();
        var requests = 0;
        var provider = new StubProvider(async (query, token) =>
        {
            if (Interlocked.Increment(ref requests) == 4) firstFour.TrySetResult();
            await release.Task.WaitAsync(token);
            return int.Parse(query.Split(' ')[1]) % 4 == 1 ? new List<ScraperSearchResult>() : Matches(query);
        });
        using var vm = CreateVm(Games(64), provider);
        vm.OnItemScrapedAsync = (item, _) => (int.Parse(item.Title.Split(' ')[1]) % 4) switch
        {
            2 => throw new IOException("Fixture import failed"),
            3 => Task.FromResult(BulkScrapeImportOutcome.Skipped),
            _ => Task.FromResult(BulkScrapeImportOutcome.Imported)
        };
        var running = vm.StartCommand.ExecuteAsync(null);
        try
        {
            await firstFour.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, requests);
        }
        finally { release.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        AssertSummary(vm, 64, imported: 16, noMatch: 16, skipped: 16, failed: 16);
        Assert.Equal(BulkScrapeRunStatus.CompletedWithErrors, vm.RunStatus);
        Assert.Equal(64, requests);
    }

    [Theory]
    [InlineData(ScraperType.ScreenScraper)]
    [InlineData(ScraperType.TheGamesDB)]
    public async Task Run_SequentialProvidersKeepTheirPolicyAnd250MsDelay(ScraperType scraperType)
    {
        var active = 0;
        var maximumActive = 0;
        var delays = new List<int>();
        var provider = new StubProvider(async (query, _) =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return Matches(query);
        });
        using var vm = CreateVm(Games(4), provider, scraperType, delay: (ms, _) =>
        {
            delays.Add(ms);
            return Task.CompletedTask;
        });
        await vm.StartCommand.ExecuteAsync(null);
        AssertSummary(vm, 4, imported: 4);
        Assert.Equal(1, maximumActive);
        Assert.Equal([250, 250, 250, 250], delays);
    }

    [Fact]
    public async Task Run_RepeatedItemReferencesAreProcessedOnceAndRestartResetsCounters()
    {
        var root = Games(2);
        root.Items.Add(root.Items[0]);
        root.Children.Add(new MediaNode("Other") { Items = new() { root.Items[0] } });
        var provider = new StubProvider();
        using var vm = CreateVm(root, provider);
        await vm.StartCommand.ExecuteAsync(null);
        AssertSummary(vm, 2, imported: 2);
        vm.OnItemScrapedAsync = (_, _) => Task.FromResult(BulkScrapeImportOutcome.Skipped);
        await vm.StartCommand.ExecuteAsync(null);
        AssertSummary(vm, 2, skipped: 2);
        Assert.Equal(4, provider.SearchCount);
    }

    [Fact]
    public async Task Dispose_CancelsSearchAndSuppressesQueuedLogAndFutureUiUpdates()
    {
        var entered = NewSignal();
        var provider = new StubProvider(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new List<ScraperSearchResult>();
        });
        var posted = new ConcurrentQueue<Action>();
        using var vm = CreateVm(Games(3), provider, ScraperType.ScreenScraper, post: posted.Enqueue);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);
        var running = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();
        var afterDispose = Volatile.Read(ref notifications);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        while (posted.TryDequeue(out var callback)) callback();
        Assert.Equal(afterDispose, notifications);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
        Assert.Empty(vm.LogText);
    }

    [Fact]
    public async Task Dispose_DuringImportAllowsOwnedCallbackToFinishWithoutUpdatingDetachedUi()
    {
        var root = Games(3);
        using var vm = CreateVm(root, new StubProvider(), ScraperType.ScreenScraper);
        var entered = NewSignal();
        var finish = NewSignal();
        var calls = 0;
        vm.OnItemScrapedAsync = async (item, _) =>
        {
            calls++;
            entered.TrySetResult();
            await finish.Task;
            item.Description = "applied";
            return BulkScrapeImportOutcome.Imported;
        };
        var running = vm.StartCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Dispose();
        var changes = 0;
        vm.PropertyChanged += (_, _) => changes++;
        finish.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, changes);
        Assert.Equal(1, calls);
        Assert.Equal("applied", root.Items[0].Description);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Fact]
    public async Task Dispose_BeforeQueuedUiInitializationPreventsAllLaterNotificationsAndConnections()
    {
        var settings = new AppSettings();
        settings.Scrapers.Add(new ScraperConfig { Type = ScraperType.ScreenScraper });
        var queued = new ConcurrentQueue<Action>();
        var connections = 0;
        using var vm = new BulkScrapeViewModel(Games(3), settings, (_, _) =>
        {
            connections++;
            return Task.FromResult<IMetadataProvider?>(new StubProvider());
        }, action =>
        {
            var completion = NewSignal();
            queued.Enqueue(() =>
            {
                try { action().GetAwaiter().GetResult(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return completion.Task;
        }, queued.Enqueue);
        var notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        var running = vm.StartCommand.ExecuteAsync(null);
        vm.Dispose();
        while (queued.TryDequeue(out var callback)) callback();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        while (queued.TryDequeue(out var callback)) callback();
        Assert.Equal(0, notifications);
        Assert.Equal(0, connections);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Theory]
    [InlineData("en", "Imported: 2")]
    [InlineData("de", "Importiert: 2")]
    public async Task SummaryText_ShowsLocalizedCounts(string culture, string importedText)
    {
        var original = Retromind.Resources.Strings.Culture;
        try
        {
            Retromind.Resources.Strings.Culture = CultureInfo.GetCultureInfo(culture);
            using var vm = CreateVm(Games(2), new StubProvider());
            await vm.StartCommand.ExecuteAsync(null);
            Assert.Contains(importedText, vm.SummaryText);
            Assert.Contains('\n', vm.SummaryText);
            Assert.True(vm.HasSummary);
        }
        finally { Retromind.Resources.Strings.Culture = original; }
    }

    [Fact]
    public async Task Aggregation_ParallelDuplicateCompletionsCannotCountAnItemTwice()
    {
        var progress = new BulkScrapeRunProgress();
        var items = Games(400).Items.ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, 4000), (index, _) =>
        {
            progress.Complete(items[index % 400], (BulkScrapeItemOutcome)(index % 4));
            return ValueTask.CompletedTask;
        });
        Assert.Equal(new BulkScrapeRunSummary(400, 400, 100, 100, 100, 100, 0, true), progress.Snapshot(400, true));
    }

    [Fact]
    public async Task Run_UnexpectedFailureDuringProcessingHasCriticalCompletionAndRetainsFinishedCounts()
    {
        using var vm = CreateVm(Games(3), new StubProvider(), ScraperType.ScreenScraper,
            delay: (_, _) => throw new IOException("Fixture processing infrastructure failed"));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(BulkScrapeRunStatus.Failed, vm.RunStatus);
        AssertSummary(vm, 3, imported: 1, notProcessed: 2);
        Assert.False(vm.IsBusy);
        Assert.Null(CancellationSource(vm));
    }

    [Theory]
    [InlineData("disabled", 0, 0)]
    [InlineData("no-url", 0, 0)]
    [InlineData("existing", 0, 0)]
    [InlineData("download-failure", 1, 1)]
    public async Task AssetImport_ReportsActualFailureSeparatelyFromPolicySkips(string mode, int expectedRequests, int failures)
    {
        using var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        typeof(MainWindowViewModel).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, client);
        var item = new MediaItem("Fixture");
        if (mode == "existing") item.Assets.Add(new MediaAsset { Type = AssetType.Cover });
        var failed = new List<AssetType>();
        var task = (Task<bool>)typeof(MainWindowViewModel)
            .GetMethod("TryImportScrapedAssetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [item, new List<string> { "Games" }, AssetType.Cover,
                mode == "no-url" ? null : "https://images.example/fixture.png", mode != "disabled", false,
                (Action<AssetType>)failed.Add])!;

        Assert.False(await task);
        Assert.Equal(expectedRequests, handler.RequestCount);
        Assert.Equal(failures, failed.Count);
        Assert.All(failed, type => Assert.Equal(AssetType.Cover, type));
    }

    private static BulkScrapeViewModel CreateVm(MediaNode node, StubProvider provider,
        ScraperType type = ScraperType.IGDB,
        Func<string, CancellationToken, Task<IMetadataProvider?>>? getProvider = null,
        Action<Action>? post = null, Func<int, CancellationToken, Task>? delay = null)
    {
        var settings = new AppSettings();
        settings.Scrapers.Add(new ScraperConfig { Name = "Fixture", Type = type });
        var vm = new BulkScrapeViewModel(node, settings,
            getProvider ?? ((_, _) => Task.FromResult<IMetadataProvider?>(provider)),
            action => action(), post ?? (action => action()), delay ?? ((_, _) => Task.CompletedTask));
        vm.OnItemScrapedAsync = (_, _) => Task.FromResult(BulkScrapeImportOutcome.Imported);
        return vm;
    }

    private static MediaNode Games(int count)
    {
        var root = new MediaNode("Games");
        for (var index = 1; index <= count; index++) root.Items.Add(new MediaItem($"Game {index}"));
        return root;
    }

    private static List<ScraperSearchResult> Matches(string query) => new() { new() { Id = query, Title = query } };
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? CancellationSource(BulkScrapeViewModel vm) => typeof(BulkScrapeViewModel)
        .GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);

    private static void AssertSummary(BulkScrapeViewModel vm, int total, int imported = 0, int noMatch = 0,
        int skipped = 0, int failed = 0, int notProcessed = 0, bool collectionComplete = true)
    {
        var completed = imported + noMatch + skipped + failed;
        Assert.Equal(new BulkScrapeRunSummary(total, completed, imported, noMatch, skipped, failed, notProcessed,
            collectionComplete), vm.Summary);
        Assert.Equal(total, completed + notProcessed);
    }

    private sealed class StubProvider : IMetadataProvider, IBulkMetadataProvider
    {
        private readonly Func<string, CancellationToken, Task<List<ScraperSearchResult>>> _search;
        private int _searchCount;
        public int SearchCount => _searchCount;
        public StubProvider(Func<string, CancellationToken, Task<List<ScraperSearchResult>>>? search = null) =>
            _search = search ?? ((query, _) => Task.FromResult(Matches(query)));
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Bulk-search capability must be preserved.");
        public Task<List<ScraperSearchResult>> SearchForBulkAsync(string query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searchCount);
            return _search(query, cancellationToken);
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            // Stop at authentication so this test needs neither network nor an Avalonia event loop.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
