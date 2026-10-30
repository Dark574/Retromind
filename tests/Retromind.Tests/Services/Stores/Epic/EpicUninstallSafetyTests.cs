using System.Collections.ObjectModel;
using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class EpicUninstallSafetyTests
{
    [Fact]
    public async Task PrefixDeletionFailure_PreservesPrefixButCommitsUninstallBeforeCleanup()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var prefix = item.PrefixPath;
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = CreateService(new Backend(), errors, deletePrefix: (_, _) =>
        {
            AssertUninstalled(item);
            throw new UnauthorizedAccessException("cannot delete prefix");
        });
        service.WriteMarker(InstallPath(temp), item);

        var result = await service.UninstallAsync(item);

        Assert.True(result.CleanupFailed);
        Assert.True(result.PrefixPreserved);
        Assert.False(result.CleanupCancelled);
        Assert.Equal(prefix, item.PrefixPath);
        Assert.True(File.Exists(temp.GetPath("Library", "Prefixes", "epic_ExampleGame", "save.dat")));
        Assert.Null(result.PreservedInstallDirectory);
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreUninstallRemainingInstallPath));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Epic.UninstallCleanupFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal("DeletePrefix", report.Context!["Operation"]);
        Assert.Equal("True", report.Context["MainGameUninstalled"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallDirectoryCleanupFailure_KeepsRecoveryPathAndDoesNotTouchPrefix(bool markerFailure)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var storedPath = item.CustomFields[CustomFieldKeyHelper.StoreInstallPath];
        var prefix = item.PrefixPath;
        var errors = new RecordingErrorService();
        void Fail(string _)
        {
            AssertUninstalled(item);
            throw new IOException("cleanup failed");
        }
        var service = CreateService(new Backend(), errors,
            deleteMarker: markerFailure ? Fail : null, deleteDirectory: markerFailure ? null : Fail);
        service.WriteMarker(InstallPath(temp), item);

        var result = await service.UninstallAsync(item);

        Assert.True(result.CleanupFailed);
        Assert.True(result.PrefixPreserved);
        Assert.Equal(InstallPath(temp), result.PreservedInstallDirectory);
        Assert.Equal(storedPath, item.CustomFields[CustomFieldKeyHelper.StoreUninstallRemainingInstallPath]);
        Assert.Equal(prefix, item.PrefixPath);
        Assert.True(File.Exists(temp.GetPath("Library", "Prefixes", "epic_ExampleGame", "save.dat")));
        Assert.Equal(markerFailure, File.Exists(Path.Combine(InstallPath(temp), ".retromind-install.json")));
        var report = Assert.Single(errors.Reports);
        Assert.Equal(markerFailure ? "RemoveInstallMarker" : "RemoveEmptyInstallDirectory", report.Context!["Operation"]);
        Assert.Equal("Epic.UninstallCleanupFailed", report.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegendaryFailureOrUnconfirmedRemoval_DoesNotClearMainGameState(bool stillRegistered)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var before = JsonSerializer.Serialize(item);
        var errors = new RecordingErrorService();
        var backend = new Backend { FailMain = !stillRegistered, KeepMainRegistered = stillRegistered };
        var service = CreateService(backend, errors);
        service.WriteMarker(InstallPath(temp), item);
        var saves = 0;

        await Assert.ThrowsAsync<LegendaryCliException>(() => MainWindowViewModel.UninstallEpicAndPersistAsync(
            service, item, true, () => { saves++; return Task.FromResult(true); }));

        Assert.Equal(before, JsonSerializer.Serialize(item));
        Assert.True(EpicMediaItemStateHelper.CanUninstall(item));
        Assert.True(Directory.Exists(InstallPath(temp)));
        Assert.True(File.Exists(temp.GetPath("Library", "Prefixes", "epic_ExampleGame", "save.dat")));
        Assert.Equal(0, saves);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Epic.UninstallFailed", report.Code);
        Assert.Equal("False", report.Context!["MainGameUninstalled"]);
        Assert.Equal("UninstallMainGame", report.Context["Operation"]);
        Assert.Null(report.Exception);
        Assert.DoesNotContain("unlabeled-credential", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task DlcRemovedButMainGameFails_PreservesMainStateAndReportsPartialOutcome()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var before = JsonSerializer.Serialize(item);
        var backend = new Backend(withDlc: true) { FailMain = true };
        var errors = new RecordingErrorService();
        var service = CreateService(backend, errors);
        service.WriteMarker(InstallPath(temp), item);

        await Assert.ThrowsAsync<LegendaryCliException>(() => service.UninstallAsync(item));

        Assert.Equal(new[] { "ExampleDlc", "ExampleGame" }, backend.UninstallAttempts);
        Assert.DoesNotContain("ExampleDlc", backend.Installed);
        Assert.Contains("ExampleGame", backend.Installed);
        Assert.Equal(before, JsonSerializer.Serialize(item));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("1", report.Context!["RemovedDlcCount"]);
        Assert.Equal("False", report.Context["MainGameUninstalled"]);
    }

    [Fact]
    public async Task CancellationAfterDlcRemoval_IsPartialAndDoesNotInvokeMainUninstall()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        using var cts = new CancellationTokenSource();
        var backend = new Backend(withDlc: true) { AfterDlcConfirmation = cts.Cancel };
        var errors = new RecordingErrorService();
        var service = CreateService(backend, errors);
        service.WriteMarker(InstallPath(temp), item);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UninstallAsync(item, ct: cts.Token));

        Assert.Equal(new[] { "ExampleDlc" }, backend.UninstallAttempts);
        Assert.True(EpicMediaItemStateHelper.CanUninstall(item));
        Assert.Equal("Epic.UninstallInterrupted", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task CancellationDuringPrefixCleanup_ReturnsConfirmedUninstallWithPreservedPartialPrefix()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        using var cts = new CancellationTokenSource();
        var errors = new RecordingErrorService();
        var service = CreateService(new Backend(), errors, deletePrefix: (path, ct) =>
        {
            File.Delete(Path.Combine(path, "save.dat"));
            File.WriteAllText(Path.Combine(path, "remaining.dat"), "retained");
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        service.WriteMarker(InstallPath(temp), item);

        var result = await service.UninstallAsync(item, ct: cts.Token);

        AssertUninstalled(item);
        Assert.True(result.CleanupCancelled);
        Assert.False(result.CleanupFailed);
        Assert.True(result.PrefixPreserved);
        Assert.NotNull(item.PrefixPath);
        Assert.True(File.Exists(temp.GetPath("Library", "Prefixes", "epic_ExampleGame", "remaining.dat")));
        Assert.Equal("Epic.UninstallCleanupCancelled", Assert.Single(errors.Reports).Code);
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("PrefixFailure")]
    [InlineData("DirectoryFailure")]
    [InlineData("Cancelled")]
    public async Task ViewModelBoundary_PersistsConfirmedUninstallAndRecoveryMetadataEvenAfterCancellation(string mode)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var prefix = item.PrefixPath;
        var storedInstallPath = item.CustomFields[CustomFieldKeyHelper.StoreInstallPath];
        var roots = new ObservableCollection<MediaNode> { new("Games") { Items = { item } } };
        var data = new MediaDataService();
        await data.SaveJsonAsync(data.Serialize(roots));
        using var cts = new CancellationTokenSource();
        var backend = new Backend { AfterMainConfirmation = mode == "Cancelled" ? cts.Cancel : null };
        var service = CreateService(backend,
            deletePrefix: mode == "PrefixFailure" ? (_, _) => throw new IOException() : null,
            deleteDirectory: mode == "DirectoryFailure" ? _ => throw new IOException() : null);
        service.WriteMarker(InstallPath(temp), item);
        var saves = 0;

        var (result, saved) = await MainWindowViewModel.UninstallEpicAndPersistAsync(service, item, true, async () =>
        {
            saves++;
            AssertUninstalled(item);
            Assert.Equal(mode == "Cancelled", cts.IsCancellationRequested);
            await data.SaveJsonAsync(data.Serialize(data.CreateSnapshot(roots)));
            return true;
        }, ct: cts.Token);

        Assert.True(saved);
        Assert.Equal(1, saves);
        var reloaded = Assert.Single(Assert.Single(await data.LoadAsync()).Items);
        AssertUninstalled(reloaded);
        Assert.Equal(mode == "Success" ? null : prefix, reloaded.PrefixPath);
        Assert.Equal(mode is "DirectoryFailure" or "Cancelled",
            reloaded.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreUninstallRemainingInstallPath));
        if (mode is "DirectoryFailure" or "Cancelled")
            Assert.Equal(storedInstallPath, reloaded.CustomFields[CustomFieldKeyHelper.StoreUninstallRemainingInstallPath]);
        Assert.Equal(mode is "PrefixFailure" or "DirectoryFailure", result.CleanupFailed);
        Assert.Equal(mode == "Cancelled", result.CleanupCancelled);
    }

    [Fact]
    public async Task PersistenceFailure_IsNotReportedAsUndoingConfirmedUninstall()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var service = CreateService(new Backend());
        service.WriteMarker(InstallPath(temp), item);

        var (_, saved) = await MainWindowViewModel.UninstallEpicAndPersistAsync(service, item, true,
            () => Task.FromResult(false));

        Assert.False(saved);
        AssertUninstalled(item);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefixCoveringInstallDirectory_IsPreservedWithUnknownFiles(bool ancestor)
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var install = InstallPath(temp);
        item.PrefixPath = Path.GetRelativePath(AppPaths.LibraryRoot, ancestor ? Path.GetDirectoryName(install)! : install);
        var service = CreateService(new Backend());
        service.WriteMarker(install, item);
        var userFile = temp.CreateFile("Library/Games/Epic/ExampleGame/mod.dat", "unknown file");

        var result = await service.UninstallAsync(item);

        AssertUninstalled(item);
        Assert.True(result.PrefixPreserved);
        Assert.False(result.CleanupFailed);
        Assert.Equal(install, result.PreservedInstallDirectory);
        Assert.Equal("unknown file", File.ReadAllText(userFile));
    }

    [Fact]
    public async Task ChangedInstallDirectoryOwnership_AfterLegendaryConfirmation_IsNotDeleted()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        var backend = new Backend { AfterMainConfirmation = () => File.Delete(Path.Combine(InstallPath(temp), ".retromind-install.json")) };
        var errors = new RecordingErrorService();
        var service = CreateService(backend, errors);
        service.WriteMarker(InstallPath(temp), item);
        var sentinel = temp.CreateFile("Library/Games/Epic/ExampleGame/mod.dat", "keep");

        var result = await service.UninstallAsync(item);

        AssertUninstalled(item);
        Assert.True(result.CleanupFailed);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Equal(InstallPath(temp), result.PreservedInstallDirectory);
        Assert.Equal("Epic.UninstallCleanupFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task LinkedInstallRoot_IsRejectedBeforeLegendaryAndPreservesExternalFiles()
    {
        using var external = new TemporaryDirectory();
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp);
        var item = CreateInstalledItem(temp);
        Directory.Delete(InstallPath(temp));
        Directory.CreateSymbolicLink(InstallPath(temp), external.RootPath);
        var sentinel = external.CreateFile("must-remain.dat", "external");
        var backend = new Backend();
        var errors = new RecordingErrorService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(backend, errors).UninstallAsync(item));

        Assert.Empty(backend.UninstallAttempts);
        Assert.True(EpicMediaItemStateHelper.CanUninstall(item));
        Assert.Equal("external", File.ReadAllText(sentinel));
        Assert.Equal("ValidateInstallDirectory", Assert.Single(errors.Reports).Context!["Operation"]);
    }

    private static MediaItem CreateInstalledItem(TemporaryDirectory temp)
    {
        temp.CreateDirectory("Library", "Games", "Epic", "ExampleGame");
        temp.CreateFile("Library/Prefixes/epic_ExampleGame/save.dat", "prefix contents");
        return new MediaItem("Example Game")
        {
            PrefixPath = Path.Combine("Prefixes", "epic_ExampleGame"),
            Files = [new MediaFileRef { Kind = MediaFileKind.Absolute, Path = Path.Combine(InstallPath(temp), "Game.exe") }],
            MediaType = MediaType.Emulator,
            EmulatorId = "wine",
            LauncherPath = "umu-run",
            LauncherArgs = "{file}",
            WorkingDirectory = InstallPath(temp),
            RunnerVersionId = "runner",
            CustomFields = new Dictionary<string, string>
            {
                [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic,
                [CustomFieldKeyHelper.StoreGameId] = "ExampleGame",
                [CustomFieldKeyHelper.StoreInstallPath] = Path.GetRelativePath(AppPaths.DataRoot, InstallPath(temp)),
                [CustomFieldKeyHelper.StoreInstalledVersion] = "1.0"
            }
        };
    }

    private static void AssertUninstalled(MediaItem item)
    {
        Assert.False(EpicMediaItemStateHelper.CanUninstall(item));
        Assert.False(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
        Assert.False(EpicMediaItemStateHelper.IsLaunchReady(item));
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreInstalledVersion));
        Assert.Empty(item.Files);
        Assert.Equal(MediaType.Command, item.MediaType);
        Assert.Null(item.EmulatorId);
        Assert.Null(item.LauncherPath);
        Assert.Null(item.RunnerVersionId);
    }

    private static string InstallPath(TemporaryDirectory temp) => temp.GetPath("Library", "Games", "Epic", "ExampleGame");
    private static EnvironmentVariableScope UseDataRoot(TemporaryDirectory temp) =>
        new(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
    private static EpicInstallService CreateService(Backend backend, RecordingErrorService? errors = null,
        Action<string, CancellationToken>? deletePrefix = null, Action<string>? deleteMarker = null, Action<string>? deleteDirectory = null)
        => new(new LegendaryCliClient(backend), errors ?? new RecordingErrorService(), deletePrefix, deleteMarker, deleteDirectory);

    private sealed class Backend : ILegendaryCliProcessRunner
    {
        internal HashSet<string> Installed { get; } = new(StringComparer.Ordinal) { "ExampleGame" };
        internal List<string> UninstallAttempts { get; } = [];
        internal bool FailMain { get; init; }
        internal bool KeepMainRegistered { get; init; }
        internal Action? AfterMainConfirmation { get; init; }
        internal Action? AfterDlcConfirmation { get; init; }
        private readonly bool _withDlc;
        private bool _mainAttempted;
        private bool _dlcAttempted;

        internal Backend(bool withDlc = false)
        {
            _withDlc = withDlc;
            if (withDlc) Installed.Add("ExampleDlc");
        }

        public Task<LegendaryCliResult> RunAsync(LegendaryCliCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (command.Arguments[0] == "list")
            {
                var catalog = JsonSerializer.Serialize(new[] { new
                {
                    app_name = "ExampleGame", app_title = "Game",
                    dlcs = _withDlc ? new[] { new { app_name = "ExampleDlc", app_title = "DLC" } } : []
                } });
                return Task.FromResult(new LegendaryCliResult(0, catalog, ""));
            }
            if (command.Arguments[0] == "list-installed")
            {
                if (_mainAttempted && !Installed.Contains("ExampleGame")) AfterMainConfirmation?.Invoke();
                else if (_dlcAttempted && !Installed.Contains("ExampleDlc")) AfterDlcConfirmation?.Invoke();
                var installed = JsonSerializer.Serialize(Installed.Select(app => new
                {
                    app_name = app, title = app, install_path = "/test-install", executable = "Game.exe", version = "1.0", is_dlc = app == "ExampleDlc"
                }));
                return Task.FromResult(new LegendaryCliResult(0, installed, ""));
            }
            var appName = command.Arguments[2];
            UninstallAttempts.Add(appName);
            if (appName == "ExampleGame")
            {
                _mainAttempted = true;
                if (FailMain) return Task.FromResult(new LegendaryCliResult(1, "", "unlabeled-credential"));
                if (!KeepMainRegistered) Installed.Remove(appName);
            }
            else
            {
                _dlcAttempted = true;
                Installed.Remove(appName);
            }
            return Task.FromResult(new LegendaryCliResult(0, "", ""));
        }
    }
}
