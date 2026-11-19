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

        // The obstruction prevents both the settings replacement and the settings rollback.
        var error = await Assert.ThrowsAsync<AggregateException>(() => UiThreadHelper.InvokeAsync(
            () => state.Coordinator.RestoreAsync(backup, MetadataRestoreMode.LibraryAndSettings)));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal("Current library", Assert.Single(await state.Library.LoadAsync()).Name);
        Assert.Equal(111, (await state.SettingsService.LoadAsync()).ItemWidth);
        Assert.False(state.Coordinator.RequiresRestart);
        Assert.True(state.Tracker.IsTracking);

        Directory.Delete(blockedPath);
        state.Settings.ItemWidth = 345;
        await state.SettingsService.SaveAsync(state.Settings);
        Assert.Equal(345, (await state.SettingsService.LoadAsync()).ItemWidth);
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
            Coordinator = new DesktopMetadataBackupCoordinator(
                Backups, Library, SettingsService, Tracker, Settings, () => Roots, () => false,
                () => SettingsSaveCancelled = true);
        }

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
