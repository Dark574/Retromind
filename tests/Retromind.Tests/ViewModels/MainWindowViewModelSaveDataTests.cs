using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelSaveDataTests
{
    [Fact]
    public async Task SaveData_LibraryLoadFailureNeverClaimsFullSuccessButCanWriteHealthySettings()
    {
        using var state = new PersistenceState();
        var libraryPath = state.Temp.CreateFile("retromind_tree.json", "unreadable library");
        Set(state.Vm, "_libraryLoadFailed", true);
        state.Dirty();

        var result = await state.Save();

        Assert.False(result.AllSaved);
        Assert.True(result.SettingsSaved); // Settings-secret commit must not roll back an actual settings write.
        Assert.Equal(0, state.LibraryWrites);
        Assert.True(state.Tracker.IsDirty);
        Assert.Equal("unreadable library", await File.ReadAllTextAsync(libraryPath));
        Assert.True(File.Exists(state.Temp.GetPath("app_settings.json")));
        Assert.Empty(state.Errors.Reports);
    }

    [Fact]
    public async Task SaveData_UntrackedLibraryDoesNotCountAsSaved()
    {
        using var state = new PersistenceState();
        state.Tracker.StopTracking();

        var result = await state.Save();

        Assert.False(result.AllSaved);
        Assert.True(result.SettingsSaved);
        Assert.Equal(0, state.LibraryWrites);
    }

    [Fact]
    public async Task SaveData_SettingsLoadFailurePreservesProtectedDataAndSavesHealthyLibrary()
    {
        using var state = new PersistenceState();
        var path = state.Temp.CreateDirectory("app_settings.json");
        await Assert.ThrowsAsync<SettingsLoadException>(() => state.Settings.LoadAsync());
        state.Dirty();

        Assert.False(await state.Vm.SaveData()); // No snapshot is requested for the protected settings store.

        Assert.Equal(1, state.LibraryWrites);
        Assert.False(state.Tracker.IsDirty);
        Assert.True(Directory.Exists(path));
        Assert.True(state.Settings.HasLoadFailure);
        Assert.Empty(state.Errors.Reports);
    }

    [Fact]
    public async Task SaveData_LibraryFailureReportsOnceRetainsDirtyStateAndLaterRetrySucceeds()
    {
        using var state = new PersistenceState();
        state.Dirty();
        var version = state.Tracker.DirtyVersion;
        state.LibraryFailure = new IOException("Disk full");

        Assert.False(await state.Vm.SaveData()); // Throws before reaching the UI settings snapshot.
        Assert.True(state.Tracker.IsDirty);
        Assert.Equal(version, state.Tracker.DirtyVersion);
        Assert.Single(state.Errors.Reports);
        Assert.Single(state.Errors.Notifications);
        Assert.False(File.Exists(state.Temp.GetPath("app_settings.json")));

        state.LibraryFailure = null;
        Assert.True((await state.Save()).AllSaved);
        Assert.False(state.Tracker.IsDirty);
        Assert.Equal(0, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));

        state.Dirty();
        state.LibraryFailure = new IOException("Disk full again");
        Assert.False(await state.Vm.SaveData());
        Assert.Equal(2, state.Errors.Reports.Count);
        Assert.Equal(2, state.Errors.Notifications.Count);
    }

    [Fact]
    public async Task SaveData_SettingsWriteFailureCanBeRetriedWithoutRepeatingCleanLibraryWrite()
    {
        using var state = new PersistenceState();
        var blockedPath = state.Temp.CreateDirectory("app_settings.json");
        state.Dirty();

        Assert.False((await state.Save()).AllSaved);
        Assert.False((await state.Save()).SettingsSaved);
        Assert.False(state.Tracker.IsDirty);
        Assert.Equal(1, state.LibraryWrites);
        Assert.Equal(2, state.Errors.Reports.Count); // One diagnosis per actual failed attempt.
        Assert.Single(state.Errors.Notifications);
        Assert.True(Get<bool>(state.Vm, "_settingsSaveFailed"));

        Directory.Delete(blockedPath);
        Assert.True((await state.Save()).AllSaved);
        Assert.False(Get<bool>(state.Vm, "_settingsSaveFailed"));
        Assert.Equal(0, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
        Assert.Equal(1, state.LibraryWrites);
    }

    [Fact]
    public async Task SettingsSuccessDoesNotResolveOpenLibraryFailureOrAllowDuplicateNotice()
    {
        using var state = new PersistenceState();
        state.Dirty();
        state.LibraryFailure = new IOException("Library write failed");
        Assert.False(await state.Vm.SaveData());

        // Exercise the same write/completion path used by the settings-only debounce.
        await state.SaveSettingsOnly();
        Assert.True(Get<bool>(state.Vm, "_librarySaveFailed"));
        Assert.Equal(1, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
        Assert.False(await state.Vm.SaveData());
        Assert.Equal(2, state.Errors.Reports.Count);
        Assert.Single(state.Errors.Notifications);

        state.LibraryFailure = null;
        Assert.True((await state.Save()).AllSaved);
        Assert.Equal(0, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
    }

    [Fact]
    public async Task LibrarySuccessDoesNotResolveOpenSettingsFailure()
    {
        using var state = new PersistenceState();
        Call(state.Vm, "OnSettingsSaveFailed", new IOException("Settings failed"));
        state.Dirty();

        await state.Tracker.SaveIfDirtyAsync(force: false);

        Assert.True(Get<bool>(state.Vm, "_settingsSaveFailed"));
        Assert.Equal(1, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
        Assert.True((await state.Save()).AllSaved);
        Assert.Equal(0, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
    }

    [Fact]
    public async Task OlderSettingsWriteDoesNotClearNewerFailure()
    {
        using var state = new PersistenceState();
        var snapshot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = state.Vm.SaveDataWithStatusAsync(() => snapshot.Task);
        Call(state.Vm, "OnSettingsSaveFailed", new IOException("A newer save failed"));
        snapshot.SetResult(state.Settings.Serialize(new AppSettings()));

        var result = await save;

        Assert.True(result.SettingsSaved);
        Assert.False(result.AllSaved);
        Assert.True(Get<bool>(state.Vm, "_settingsSaveFailed"));
        Assert.Equal(1, Get<int>(state.Vm, "_persistenceErrorNoticeShown"));
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task SaveData_NewLibraryChangesDuringWritePreventFullSuccessAndRemainRetryable()
    {
        using var state = new PersistenceState();
        state.Dirty();
        state.DuringLibraryWrite = () => state.Dirty();

        Assert.False((await state.Save()).AllSaved);
        Assert.True(state.Tracker.IsDirty);
        Assert.Empty(state.Errors.Reports); // A newer edit is not an IO error.

        state.DuringLibraryWrite = null;
        Assert.True((await state.Save()).AllSaved);
        Assert.False(state.Tracker.IsDirty);
    }

    [Fact]
    public async Task SaveData_NewSettingsEditDuringSnapshotPreventsFullSuccess()
    {
        using var state = new PersistenceState();
        var result = await state.Vm.SaveDataWithStatusAsync(() =>
        {
            Set(state.Vm, "_settingsDirtyVersion", 1);
            return Task.FromResult(state.Settings.Serialize(new AppSettings()));
        });

        Assert.False(result.AllSaved);
        Assert.True(result.SettingsSaved);
        Assert.Empty(state.Errors.Reports);
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task SaveData_CancellationKeepsLibraryDirtyWithoutErrorDialog()
    {
        using var state = new PersistenceState();
        state.Dirty();
        state.LibraryFailure = new OperationCanceledException();

        Assert.False(await state.Vm.SaveData());
        Assert.True(state.Tracker.IsDirty);
        Assert.Empty(state.Errors.Reports);
        Assert.Empty(state.Errors.Notifications);

        state.LibraryFailure = null;
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task SaveData_RestoreBlockDoesNotReportSuccessOrShowAnotherDialog()
    {
        using var state = new PersistenceState();
        using (await state.Settings.BeginRestoreAsync())
        {
            Assert.False((await state.Save()).AllSaved);
            Assert.Empty(state.Errors.Reports);
        }
        Assert.True((await state.Save()).AllSaved);
    }

    private sealed class PersistenceState : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        private readonly EnvironmentVariableScope _environment;
        public SettingsService Settings { get; } = new();
        public RecordingErrors Errors { get; } = new();
        public MainWindowViewModel Vm { get; }
        public LibraryChangeTracker Tracker { get; }
        public int LibraryWrites { get; private set; }
        public Exception? LibraryFailure { get; set; }
        public Action? DuringLibraryWrite { get; set; }

        public PersistenceState()
        {
            _environment = new EnvironmentVariableScope(("APPIMAGE", Temp.GetPath("Retromind.AppImage")));
            Vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            Set(Vm, "_persistenceStateLock", new object());
            Set(Vm, "_errorService", Errors);
            Set(Vm, "_settingsService", Settings);
            Set(Vm, "_currentSettings", new AppSettings());
            Tracker = new LibraryChangeTracker(new MediaDataService(), (_, _) => { }, null, null, null, () =>
            {
                LibraryWrites++;
                DuringLibraryWrite?.Invoke();
                return LibraryFailure == null ? Task.CompletedTask : Task.FromException(LibraryFailure);
            });
            Tracker.Initialize(new ObservableCollection<MediaNode>());
            Set(Vm, "_libraryTracker", Tracker);
            Tracker.SaveFailed += exception => Call(Vm, "OnLibrarySaveFailed", exception);
            Tracker.SaveSucceeded += () => Call(Vm, "OnLibrarySaveSucceeded");
        }

        public void Dirty()
        {
            Call(Tracker, "MarkDirtyCore");
            Call(Tracker, "StopDebouncedSave"); // No asynchronous timer race in these policy tests.
        }

        public Task<(bool AllSaved, bool SettingsSaved)> Save() =>
            Vm.SaveDataWithStatusAsync(() => Task.FromResult(Settings.Serialize(new AppSettings())));

        public Task SaveSettingsOnly() => (Task)Call(Vm, "SaveSettingsSnapshotAsync", 0,
            CancellationToken.None, (Func<Task<string>>)(() => Task.FromResult(Settings.Serialize(new AppSettings()))))!;

        public void Dispose()
        {
            Tracker.StopTracking();
            _environment.Dispose();
            Temp.Dispose();
        }
    }

    private sealed class RecordingErrors : IErrorService
    {
        public List<ErrorReport> Reports { get; } = [];
        public List<Func<Task>> Notifications { get; } = [];
        public void Report(ErrorReport error, Func<Task>? notifyUser = null)
        {
            Reports.Add(error);
            if (notifyUser != null) Notifications.Add(notifyUser);
        }
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
