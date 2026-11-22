using System.Collections.ObjectModel;
using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopMetadataBackupCoordinatorTests
{
    [Theory]
    [InlineData(MetadataRestoreMode.LibraryOnly, true, 111)]
    [InlineData(MetadataRestoreMode.LibraryAndSettings, false, 234)]
    public async Task Restore_WritesSelectedDataAndProtectsItUntilRestart(
        MetadataRestoreMode mode, bool createSafetyBackup, int expectedItemWidth)
    {
        using var state = new BackupState();
        state.Settings.BackupBeforeRestore = createSafetyBackup;
        var current = await state.SaveCurrentMetadataAsync();
        var backup = await state.CreateRestoreSourceAsync();

        await UiThreadHelper.InvokeAsync(() => state.Coordinator.RestoreAsync(backup, mode));

        Assert.Equal("Restored library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.Equal(expectedItemWidth, (await state.SettingsService.LoadAsync()).ItemWidth);
        Assert.Equal("Current library", Assert.Single(state.Roots).Name);
        Assert.True(state.SettingsSaveCancelled);
        Assert.True(state.Coordinator.RequiresRestart);
        Assert.False(state.Tracker.IsTracking);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.SettingsService.SaveAsync(state.Settings));

        var safetyBackups = (await state.Backups.GetBackupsAsync())
            .Where(entry => entry.Reason == MetadataBackupReason.BeforeRestore);
        if (createSafetyBackup)
        {
            var safetyBackup = Assert.Single(safetyBackups);
            Assert.Equal(current, await state.Backups.ReadBackupAsync(safetyBackup.FilePath));
        }
        else
        {
            Assert.Empty(safetyBackups);
        }
    }

    [Fact]
    public async Task LibraryOnlyRestore_PersistsLiveSettingsWhileDebouncedSaveIsPending()
    {
        using var state = new BackupState();
        var current = await state.SaveCurrentMetadataAsync();
        var backup = await state.CreateRestoreSourceAsync();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturedSettings = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var persistence = new DesktopPersistenceCoordinator(
            state.Tracker, state.SettingsService, () => false, () =>
            {
                captureStarted.SetResult();
                return capturedSettings.Task;
            },
            new RecordingErrorService(), _ => Task.FromResult(false));
        var coordinator = state.CreateCoordinator(persistence.CancelPendingSettingsSave);

        state.Settings.ItemWidth = 222;
        var pendingSave = persistence.ScheduleSettingsSave();
        try
        {
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(111, (await state.SettingsService.LoadAsync()).ItemWidth);

            await UiThreadHelper.InvokeAsync(() => coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryOnly));
        }
        finally
        {
            // A delayed older snapshot must not win over the restore's current settings write.
            capturedSettings.TrySetResult(current.SettingsJson);
            await pendingSave.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("Restored library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.Equal(222, (await new SettingsService().LoadAsync()).ItemWidth);
        Assert.True(coordinator.RequiresRestart);
    }

    [Fact]
    public async Task LibraryOnlyRestore_SettingsWriteFailureLeavesLibraryIntact()
    {
        using var state = new BackupState();
        state.Settings.BackupBeforeRestore = false;
        var current = await state.SaveCurrentMetadataAsync();
        var backup = await state.CreateRestoreSourceAsync();
        state.Settings.ItemWidth = 222;
        var blockedPath = state.Temp.CreateDirectory("app_settings.json.tmp");

        var error = await Record.ExceptionAsync(() => UiThreadHelper.InvokeAsync(
            () => state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryOnly)));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(current.LibraryJson, await File.ReadAllTextAsync(state.Temp.GetPath("retromind_tree.json")));
        Assert.Equal(111, (await state.SettingsService.LoadAsync()).ItemWidth);
        Assert.False(state.Coordinator.RequiresRestart);
        Assert.True(state.Tracker.IsTracking);

        Directory.Delete(blockedPath);
        await state.SettingsService.SaveAsync(state.Settings);
        Assert.Equal(222, (await state.SettingsService.LoadAsync()).ItemWidth);
    }

    [Fact]
    public async Task LibraryOnlyRestore_SkipsProtectedSettingsAfterLoadFailure()
    {
        using var state = new BackupState();
        var backup = await state.CreateRestoreSourceAsync();
        var protectedPath = state.Temp.CreateDirectory("app_settings.json");
        await Assert.ThrowsAsync<SettingsLoadException>(() => state.SettingsService.LoadAsync());

        await UiThreadHelper.InvokeAsync(
            () => state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryOnly));

        Assert.Equal("Restored library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.True(Directory.Exists(protectedPath));
        Assert.True(state.SettingsService.HasLoadFailure);
        Assert.True(state.Coordinator.RequiresRestart);
    }

    [Theory]
    [InlineData("[null]", "{}")]
    [InlineData("[]", "{\"SavedSearchTerms\":null}")]
    public async Task Restore_InvalidMetadataIsRejectedBeforeCancellingSaves(string libraryJson, string settingsJson)
    {
        using var state = new BackupState();
        await state.SaveCurrentMetadataAsync();
        var backup = await state.Backups.CreateBackupAsync(new(libraryJson, settingsJson), MetadataBackupReason.Manual);

        await Assert.ThrowsAsync<JsonException>(() =>
            state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryAndSettings));

        Assert.Equal("Current library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.Equal(111, (await state.SettingsService.LoadAsync()).ItemWidth);
        Assert.False(state.SettingsSaveCancelled);
        Assert.False(state.Coordinator.RequiresRestart);
        Assert.True(state.Tracker.IsTracking);
        Assert.Single(await state.Backups.GetBackupsAsync());
    }

    [Fact]
    public async Task Restore_SettingsWriteFailureRollsBackLibraryAndReleasesSaveBlock()
    {
        using var state = new BackupState();
        state.Settings.BackupBeforeRestore = false;
        await state.SaveCurrentMetadataAsync();
        var backup = await state.CreateRestoreSourceAsync();
        var blockedPath = state.Temp.CreateDirectory("app_settings.json.tmp");

        var error = await Record.ExceptionAsync(() => UiThreadHelper.InvokeAsync(
            () => state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryAndSettings)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("Current library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.Equal(111, (await state.SettingsService.LoadAsync()).ItemWidth);
        Assert.False(state.Coordinator.RequiresRestart);
        Assert.True(state.Tracker.IsTracking);

        Directory.Delete(blockedPath);
        state.Settings.ItemWidth = 345;
        await state.SettingsService.SaveAsync(state.Settings);
        Assert.Equal(345, (await state.SettingsService.LoadAsync()).ItemWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRestore_SettingsLoadAndWriteFailureRestoresPreviousLibraryState(bool hadLibrary)
    {
        using var state = new BackupState();
        var path = state.Temp.GetPath("retromind_tree.json");
        if (hadLibrary)
            await state.Library.SaveJsonAsync(state.Library.Serialize(state.Roots));
        var previous = hadLibrary ? await File.ReadAllBytesAsync(path) : null;
        var settingsPath = state.Temp.CreateDirectory("app_settings.json");
        await Assert.ThrowsAsync<SettingsLoadException>(() => state.SettingsService.LoadAsync());
        var backup = await state.CreateRestoreSourceAsync();

        var error = await Record.ExceptionAsync(() => UiThreadHelper.InvokeAsync(
            () => state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryAndSettings)));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.False(state.Coordinator.RequiresRestart);
        Assert.True(state.Tracker.IsTracking);
        Assert.True(Directory.Exists(settingsPath));
        Assert.True(state.SettingsService.HasLoadFailure);
        if (hadLibrary)
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        else
            Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RestoreBoundary_KeepsLateStatisticsSaveBehindTrackingShutdown()
    {
        using var state = new BackupState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.SaveCurrentMetadataAsync();

        var restore = state.Tracker.RunRestoreAsync(async () =>
        {
            await state.Library.RestoreJsonAsync(state.Library.Serialize([new MediaNode("Replacement")]));
            entered.SetResult();
            await finish.Task;
            await UiThreadHelper.InvokeAsync(state.Tracker.StopTracking);
        }, flushCurrentChanges: true);
        Task lateSave;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UiThreadHelper.InvokeAsync(state.Tracker.MarkDirty);
            lateSave = state.Tracker.SaveIfDirtyAsync(force: false);
            Assert.False(lateSave.IsCompleted);
        }
        finally { finish.SetResult(); }

        await Task.WhenAll(restore, lateSave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Replacement", Assert.Single(await state.Library.LoadAsync()).Name);
    }

    private sealed class BackupState : IDisposable
    {
        private readonly EnvironmentVariableScope _environment;

        public TemporaryDirectory Temp { get; } = new();
        public MediaDataService Library { get; } = new();
        public SettingsService SettingsService { get; } = new();
        public MetadataBackupService Backups { get; }
        public AppSettings Settings { get; } = new()
        {
            ItemWidth = 111,
            EnableAutomaticMetadataBackups = true
        };
        public ObservableCollection<MediaNode> Roots { get; } = [new("Current library")];
        public LibraryChangeTracker Tracker { get; }
        public DesktopMetadataBackupCoordinator Coordinator { get; }
        public bool SettingsSaveCancelled { get; private set; }

        public BackupState()
        {
            AvaloniaTestDispatcher.EnsureStarted();
            _environment = new(("APPIMAGE", Temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            Backups = new MetadataBackupService(Temp.RootPath);
            Tracker = new LibraryChangeTracker(Library, (_, _) => { });
            Tracker.Initialize(Roots);
            Coordinator = CreateCoordinator(() => SettingsSaveCancelled = true);
        }

        public DesktopMetadataBackupCoordinator CreateCoordinator(Action cancelPendingSettingsSave) =>
            new(Backups, Library, SettingsService, Tracker, Settings, () => Roots, () => false, cancelPendingSettingsSave);

        public async Task<MetadataBackupContent> SaveCurrentMetadataAsync()
        {
            var current = new MetadataBackupContent(Library.Serialize(Roots), SettingsService.Serialize(Settings));
            await Library.SaveJsonAsync(current.LibraryJson);
            await SettingsService.SaveJsonAsync(current.SettingsJson);
            return current;
        }

        public Task<MetadataBackupInfo> CreateRestoreSourceAsync() => Backups.CreateBackupAsync(
            new MetadataBackupContent(
                Library.Serialize([new MediaNode("Restored library")]),
                SettingsService.Serialize(new AppSettings { ItemWidth = 234 })),
            MetadataBackupReason.Manual);

        public void Dispose()
        {
            Tracker.StopTracking();
            _environment.Dispose();
            Temp.Dispose();
        }
    }
}
