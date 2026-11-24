using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopDownloadWorkflowTests
{
    [Fact]
    public async Task EnablingAutomaticUpdates_ImmediatelyQueuesKnownGogAndEpicRevisionsWithoutDuplicates()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var settings = new AppSettings { Downloads = new DownloadSettings { LimitToTimeWindow = true } };
        var errors = new RecordingErrorService();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), errors, new NoonClock());
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<SettingsService>();
        App.ConfigureServices(services);
        services.AddSingleton(manager);
        using var provider = services.BuildServiceProvider();

        var gogGame = Game(StoreProviderIds.Gog, "123", temp);
        var epicGame = Game(StoreProviderIds.Epic, "EpicGame", temp);
        var excluded = Game(StoreProviderIds.Epic, "ExcludedGame", temp);
        excluded.CustomFields[CustomFieldKeyHelper.StoreAutomaticUpdatesExcluded] = "true";
        var gogRevision = new string('A', 64);
        gogGame.CustomFields[CustomFieldKeyHelper.StoreAvailableRevision] = gogRevision;
        gogGame.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "true";
        gogGame.CustomFields[CustomFieldKeyHelper.StoreUpdateLastCheckedUtc] = DateTimeOffset.UtcNow.ToString("O");
        var root = new MediaNode("Games");
        root.Items.Add(gogGame);
        root.Items.Add(epicGame);
        root.Items.Add(excluded);
        var roots = new ObservableCollection<MediaNode> { root };
        var workspace = new DesktopStoreWorkspace(settings,
            GetWindow: () => null,
            GetStorageProvider: () => null,
            GetRootItems: () => roots,
            GetMultiSelectionActive: () => false,
            GetOperationInProgress: () => true,
            SetOperationInProgress: _ => { },
            FindParentNode: item => root.Items.Contains(item) ? root : null,
            FindInheritedEmulator: _ => null,
            SaveLibraryAsync: () => Task.FromResult(true),
            MarkLibraryDirty: () => { },
            MarkLibraryDirtyAndSaveSoon: () => { },
            RefreshActions: () => { },
            RefreshItemAsync: _ => Task.CompletedTask,
            PublishImportAsync: (_, _) => Task.CompletedTask);
        var (_, epic, downloads) = provider.GetRequiredService<DesktopStoreWorkflowFactory>().Create(workspace);

        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                downloads.Start();
                var update = new LegendaryUpdateStatus("1.0", "2.0", true);
                Assert.True(epic.ApplyEpicUpdateCheckMetadata(epicGame, update));
                Assert.True(epic.ApplyEpicUpdateCheckMetadata(excluded, update));
                Assert.Empty(manager.Snapshot());

                settings.Downloads = settings.Downloads with { AutomaticUpdates = AutomaticUpdateMode.DownloadOnly };
                downloads.SettingsChanged();

                Assert.Collection(manager.Snapshot().Select(row => row.Job),
                    job =>
                    {
                        Assert.Equal(DownloadStore.Gog, job.Store);
                        Assert.Equal(gogGame.Id, job.ItemId);
                        Assert.Equal(gogRevision, job.Revision);
                    },
                    job =>
                    {
                        Assert.Equal(DownloadStore.Epic, job.Store);
                        Assert.Equal(epicGame.Id, job.ItemId);
                        Assert.Equal(epicGame.CustomFields[CustomFieldKeyHelper.StoreAvailableRevision], job.Revision);
                    });

                settings.Downloads = settings.Downloads with { AutomaticUpdates = AutomaticUpdateMode.Disabled };
                downloads.SettingsChanged();
                settings.Downloads = settings.Downloads with { AutomaticUpdates = AutomaticUpdateMode.DownloadOnly };
                downloads.SettingsChanged();

                Assert.Equal(2, manager.Snapshot().Length);

                var epicJobId = manager.Snapshot().Single(row => row.Job.ItemId == epicGame.Id).Job.Id;
                downloads.SetExcluded(epicGame, true);
                var paused = manager.Snapshot().Single(row => row.Job.Id == epicJobId).Job;
                Assert.Equal(DownloadState.Paused, paused.State);
                Assert.Equal(DownloadReason.Excluded, paused.Reason);

                downloads.SetExcluded(epicGame, false);
                Assert.Equal(DownloadReason.None, manager.Snapshot().Single(row => row.Job.Id == epicJobId).Job.Reason);
                Assert.Equal(2, manager.Snapshot().Length);

                downloads.SetExcluded(excluded, false);
                Assert.Single(manager.Snapshot(), row => row.Job.ItemId == excluded.Id);
            });
        }
        finally
        {
            await UiThreadHelper.InvokeAsync(downloads.StopAsync).WaitAsync(TimeSpan.FromSeconds(5));
            downloads.Release();
        }
        Assert.Empty(errors.Reports);
    }

    private static MediaItem Game(string provider, string gameId, TemporaryDirectory temp)
    {
        var path = temp.CreateDirectory(gameId);
        var item = new MediaItem(gameId) { LauncherPath = temp.CreateFile(gameId + "/game") };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = provider;
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = path;
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPlatform] = "linux";
        return item;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ConfirmedManualInstallation_CompletesTheProviderQueueAndClearsThePendingBadge(bool isEpic, bool saveSucceeds)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var settings = new AppSettings();
        var errors = new RecordingErrorService();
        var path = temp.GetPath("downloads.json");
        var providerId = isEpic ? StoreProviderIds.Epic : StoreProviderIds.Gog;
        var storeKind = isEpic ? DownloadStore.Epic : DownloadStore.Gog;
        var game = Game(providerId, "game-id", temp);
        var package = new GogInstallerPackage("game-id", GogInstallPlatform.Linux, "Game", "2", []);
        var revision = isEpic ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("2")))
            : GogInstallerArchive.GetRevision(package);
        var job = new DownloadJob(Guid.NewGuid(), game.Id, storeKind, DownloadKind.Update, revision,
            false, false, false, DownloadState.InstallationPending, PayloadReady: !isEpic, StoreGameId: "game-id");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([job]);
        }
        var manager = new DownloadManagerService(path, errors, TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<SettingsService>();
        App.ConfigureServices(services);
        services.AddSingleton(manager);
        using var provider = services.BuildServiceProvider();
        var root = new MediaNode("Games") { Items = [game] };
        var saves = 0;
        var workspace = new DesktopStoreWorkspace(settings, () => null, () => null, () => [root],
            () => false, () => true, _ => { }, _ => root, _ => null,
            () =>
            {
                // The queue must not report success before the library-save outcome is known.
                Assert.Equal(DownloadState.InstallationPending, Assert.Single(manager.Snapshot()).Job.State);
                saves++;
                return Task.FromResult(saveSucceeds);
            },
            () => { }, () => { }, () => { }, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var (gog, epic, downloads) = provider.GetRequiredService<DesktopStoreWorkflowFactory>().Create(workspace);
        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(_ => downloads.Start());
            Assert.Equal(!isEpic, manager.GetQueueStatus().PendingGogInstallations.Count > 0);

            var saved = isEpic ? await epic.SaveEpicInstallationAsync(game, "game-id", "2")
                : await gog.SaveGogInstallationAsync(game, package, GogInstallPlatform.Linux);

            Assert.Equal(saveSucceeds, saved);
            Assert.Equal(1, saves);
            var completed = Assert.Single(manager.Snapshot()).Job;
            Assert.Equal(DownloadState.Completed, completed.State);
            Assert.Equal(saveSucceeds ? DownloadReason.None : DownloadReason.SavePending, completed.Reason);
            Assert.Empty(manager.GetQueueStatus().PendingGogInstallations);
        }
        finally
        {
            await UiThreadHelper.InvokeAsync(downloads.StopAsync).WaitAsync(TimeSpan.FromSeconds(5));
            downloads.Release();
        }
        using var restored = new DownloadQueueStore(path);
        Assert.Equal(DownloadState.Completed, Assert.Single(restored.Load().Jobs).State);
        Assert.Empty(errors.Reports);
    }

    private sealed class NoonClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
