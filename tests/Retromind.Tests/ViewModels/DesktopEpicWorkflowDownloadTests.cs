using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopEpicWorkflowDownloadTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueuedUpdate_AlreadyCurrentClearsAndPersistsUpdateMetadata(bool saveSucceeds)
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.GetPath("game");
        var cli = new LegendaryCliClient(new CurrentGameRunner(installPath));
        var provider = new EpicProvider(cli, new EpicLibraryService(cli));
        var installer = new EpicInstallService(cli, new RecordingErrorService());
        var item = new MediaItem("Fixture");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic;
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "FixtureEpic";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = installPath;
        installer.WriteMarker(installPath, item);
        var root = new MediaNode("Games");
        root.Items.Add(item);
        var dirty = 0;
        var saves = 0;
        var refreshes = 0;
        var workspace = new DesktopStoreWorkspace(new(), () => null, () => null, () => [root],
            () => false, () => false, _ => { }, _ => root, _ => null,
            () =>
            {
                saves++;
                Assert.Equal("false", item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable]);
                Assert.DoesNotContain(CustomFieldKeyHelper.StoreAvailableRevision, item.CustomFields.Keys);
                return Task.FromResult(saveSucceeds);
            },
            () => dirty++, () => { }, () => refreshes++, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var workflow = new DesktopEpicWorkflow(workspace, provider, provider, installer, new EpicLaunchService(cli));
        workflow.ApplyEpicUpdateCheckMetadata(item, new LegendaryUpdateStatus("1", "2", true));
        var job = new DownloadJob(Guid.NewGuid(), item.Id, DownloadStore.Epic, DownloadKind.Update,
            item.CustomFields[CustomFieldKeyHelper.StoreAvailableRevision], true, false, false, DownloadState.Queued);
        var execution = new DownloadExecution(job, new(), MayInstall: true, _ => { },
            () => throw new InvalidOperationException("An already current game needs no installation."));

        var outcome = await workflow.RunQueuedUpdateAsync(item, execution, CancellationToken.None);

        Assert.Equal(DownloadState.Completed, outcome.State);
        Assert.Equal(saveSucceeds ? DownloadReason.None : DownloadReason.SavePending, outcome.Reason);
        Assert.Equal(1, dirty);
        Assert.Equal(1, saves);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task QueuedUpdate_DownloadOnlyRecordsTheResolvedTargetVersion()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.GetPath("game");
        var runner = new CurrentGameRunner(installPath, "3");
        var cli = new LegendaryCliClient(runner);
        var provider = new EpicProvider(cli, new EpicLibraryService(cli));
        var installer = new EpicInstallService(cli, new RecordingErrorService());
        var item = new MediaItem("Fixture");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic;
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "FixtureEpic";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = installPath;
        installer.WriteMarker(installPath, item);
        var root = new MediaNode("Games") { Items = [item] };
        var saves = 0;
        var workspace = new DesktopStoreWorkspace(new(), () => null, () => null, () => [root],
            () => false, () => false, _ => { }, _ => root, _ => null,
            () => { saves++; return Task.FromResult(true); },
            () => { }, () => { }, () => { }, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var workflow = new DesktopEpicWorkflow(workspace, provider, provider, installer, new EpicLaunchService(cli));
        var job = new DownloadJob(Guid.NewGuid(), item.Id, DownloadStore.Epic, DownloadKind.Update,
            "", false, false, false, DownloadState.Queued);
        var execution = new DownloadExecution(job, new(), MayInstall: false, _ => { },
            () => throw new InvalidOperationException("Installation was not requested."));
        var before = await workflow.GetQueuedUpdateCatalogAsync(CancellationToken.None);

        var outcome = await workflow.RunQueuedUpdateAsync(item, execution, CancellationToken.None);

        Assert.Equal(DownloadState.InstallationPending, outcome.State);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("3"))), outcome.ResolvedRevision);
        Assert.Equal(0, saves);
        Assert.True(workflow.IsCurrentUpdateCatalog(before));

        runner.InstalledVersion = "3";
        await workflow.SaveEpicInstallationAsync(item, "FixtureEpic", "3");

        Assert.Equal(1, saves);
        Assert.False(workflow.IsCurrentUpdateCatalog(before));
        var after = await workflow.GetQueuedUpdateCatalogAsync(CancellationToken.None);
        Assert.NotSame(before, after);
        Assert.Equal("3", after["FixtureEpic"].InstalledVersion);
        Assert.False(after["FixtureEpic"].IsUpdateAvailable);
    }

    [Fact]
    public async Task SaveInstallation_CompletesWhileCatalogQueryIsRunningAndDiscardsItsOldSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var querying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new CurrentGameRunner(temp.GetPath("game"), "3")
        {
            DelayFirstCatalog = async ct =>
            {
                querying.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        var cli = new LegendaryCliClient(runner);
        var provider = new EpicProvider(cli, new EpicLibraryService(cli));
        var root = new MediaNode("Games");
        var item = new MediaItem("Fixture");
        root.Items.Add(item);
        var saves = 0;
        var workspace = new DesktopStoreWorkspace(new(), () => null, () => null, () => [root],
            () => false, () => false, _ => { }, _ => root, _ => null,
            () => { saves++; return Task.FromResult(true); },
            () => { }, () => { }, () => { }, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var workflow = new DesktopEpicWorkflow(workspace, provider, provider,
            new EpicInstallService(cli, new RecordingErrorService()), new EpicLaunchService(cli));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = workflow.GetQueuedUpdateCatalogAsync(timeout.Token);

        try
        {
            await querying.Task.WaitAsync(timeout.Token);
            runner.InstalledVersion = "3";
            Assert.True(await workflow.SaveEpicInstallationAsync(item, "FixtureEpic", "3").WaitAsync(timeout.Token));
            Assert.Equal(1, saves);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var catalog = await pending.WaitAsync(timeout.Token);
        Assert.Equal(new LegendaryUpdateStatus("3", "3", false), catalog["FixtureEpic"]);
        Assert.True(workflow.IsCurrentUpdateCatalog(catalog));
        Assert.Same(catalog, await workflow.GetQueuedUpdateCatalogAsync(timeout.Token));
    }

    private sealed class CurrentGameRunner(string installPath, string availableVersion = "2") : ILegendaryCliProcessRunner
    {
        internal string InstalledVersion { get; set; } = "2";
        internal Func<CancellationToken, Task>? DelayFirstCatalog { get; set; }

        public async Task<LegendaryCliResult> RunAsync(LegendaryCliCommand command, CancellationToken ct = default)
        {
            var output = command.Arguments[0] switch
            {
                "status" => """{"account":"fixture","config_directory":"fixture-config"}""",
                "list-installed" => $$"""[{"app_name":"FixtureEpic","install_path":{{JsonSerializer.Serialize(installPath)}},"version":{{JsonSerializer.Serialize(InstalledVersion)}}}]""",
                "list" => JsonSerializer.Serialize(new[]
                {
                    new { app_name = "FixtureEpic", asset_infos = new { Windows = new { build_version = availableVersion } } }
                }),
                _ => throw new InvalidOperationException("Unexpected Legendary command.")
            };
            if (command.Arguments[0] == "list-installed" && DelayFirstCatalog is { } delay)
            {
                DelayFirstCatalog = null;
                await delay(ct);
            }
            return new LegendaryCliResult(0, output, string.Empty);
        }
    }
}
