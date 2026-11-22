using System.Collections.ObjectModel;
using System.Reflection;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopPersistenceCoordinatorTests
{
    [Fact]
    public async Task SaveData_LibraryLoadFailureNeverClaimsFullSuccessButCanWriteHealthySettings()
    {
        using var state = new PersistenceState();
        var libraryPath = state.Temp.CreateFile("retromind_tree.json", "unreadable library");
        state.LibraryLoadFailed = true;
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

        state.CaptureSettings = () => throw new InvalidOperationException("Protected settings must not be captured.");
        var result = await state.Save();

        Assert.False(result.AllSaved);
        Assert.False(result.SettingsSaved);
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

        Assert.Equal((false, false), await state.Save());
        Assert.True(state.Tracker.IsDirty);
        Assert.Equal(version, state.Tracker.DirtyVersion);
        Assert.Single(state.Errors.Reports);
        Assert.Single(state.Notices);
        Assert.False(File.Exists(state.Temp.GetPath("app_settings.json")));

        state.LibraryFailure = null;
        Assert.True((await state.Save()).AllSaved);
        Assert.False(state.Tracker.IsDirty);

        state.Dirty();
        state.LibraryFailure = new IOException("Disk full again");
        Assert.Equal((false, false), await state.Save());
        Assert.Equal(2, state.Errors.Reports.Count);
        Assert.Equal(2, state.Notices.Count());
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
        Assert.Single(state.Notices);

        Directory.Delete(blockedPath);
        Assert.True((await state.Save()).AllSaved);
        Assert.Equal(1, state.LibraryWrites);

        state.CaptureSettings = () => Task.FromException<string>(new IOException("Another save failed"));
        Assert.Equal((false, false), await state.Save());
        Assert.Equal(2, state.Notices.Count());
    }

    [Fact]
    public async Task SettingsSuccessDoesNotResolveOpenLibraryFailureOrAllowDuplicateNotice()
    {
        using var state = new PersistenceState();
        state.Dirty();
        state.LibraryFailure = new IOException("Library write failed");
        Assert.Equal((false, false), await state.Save());

        await state.Coordinator.ScheduleSettingsSave();
        Assert.True(File.Exists(state.Temp.GetPath("app_settings.json")));
        Assert.Equal((false, false), await state.Save());
        Assert.Equal(2, state.Errors.Reports.Count);
        Assert.Single(state.Notices);

        state.LibraryFailure = null;
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task LibrarySuccessDoesNotResolveOpenSettingsFailure()
    {
        using var state = new PersistenceState();
        state.CaptureSettings = () => Task.FromException<string>(new IOException("Settings failed"));
        Assert.Equal((false, false), await state.Save());
        state.Dirty();

        await state.Tracker.SaveIfDirtyAsync(force: false);

        Assert.False(state.Tracker.IsDirty);
        Assert.Equal((false, false), await state.Save());
        Assert.Equal(2, state.Errors.Reports.Count);
        Assert.Single(state.Notices);

        state.CaptureSettings = state.DefaultSnapshot;
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task OverlappingSettingsSaves_ReportNewerFailureAfterOlderSnapshot()
    {
        using var state = new PersistenceState();
        var snapshot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.CaptureSettings = () => snapshot.Task;
        var save = state.Save();

        state.CaptureSettings = () => Task.FromException<string>(new IOException("A newer save failed"));
        var newerSave = state.Save();
        snapshot.SetResult(state.Settings.Serialize(new AppSettings()));

        Assert.True((await save).SettingsSaved);
        Assert.Equal((false, false), await newerSave);
        Assert.Single(state.Notices);
        state.CaptureSettings = state.DefaultSnapshot;
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
        var snapshot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.CaptureSettings = () => snapshot.Task;
        var save = state.Save();

        state.CaptureSettings = state.DefaultSnapshot;
        var pendingSettingsSave = state.Coordinator.ScheduleSettingsSave();
        snapshot.SetResult(state.Settings.Serialize(new AppSettings()));
        var result = await save;

        Assert.False(result.AllSaved);
        Assert.True(result.SettingsSaved);
        Assert.Empty(state.Errors.Reports);
        await pendingSettingsSave;
        Assert.True((await state.Save()).AllSaved);
    }

    [Fact]
    public async Task ExplicitSave_WritesAfterAnInFlightDebouncedSnapshot()
    {
        using var state = new PersistenceState();
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSnapshot = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new AppSettings { SavedSearchTerms = ["new search"] };
        state.CaptureSettings = () =>
        {
            captureStarted.SetResult();
            return oldSnapshot.Task;
        };
        var debouncedSave = state.Coordinator.ScheduleSettingsSave();
        await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var explicitCaptureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.CaptureSettings = () =>
        {
            explicitCaptureStarted.SetResult();
            return Task.FromResult(state.Settings.Serialize(expected));
        };
        var explicitSave = state.Save();
        try
        {
            // If the explicit write raced ahead, deliberately let it finish before the older one.
            if (explicitCaptureStarted.Task.IsCompleted)
                await explicitSave.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            oldSnapshot.SetResult(state.Settings.Serialize(new AppSettings { SavedSearchTerms = ["old search"] }));
        }

        await debouncedSave.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((true, true), await explicitSave.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected.SavedSearchTerms, (await state.Settings.LoadAsync()).SavedSearchTerms);
        Assert.Empty(state.Errors.Reports);
    }

    [Fact]
    public async Task SaveData_CancellationKeepsLibraryDirtyWithoutErrorDialog()
    {
        using var state = new PersistenceState();
        state.Dirty();
        state.LibraryFailure = new OperationCanceledException();

        Assert.Equal((false, false), await state.Save());
        Assert.True(state.Tracker.IsDirty);
        Assert.Empty(state.Errors.Reports);
        Assert.Empty(state.Notices);

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

    [Fact]
    public async Task Flush_ForcesLibraryWriteAndCompletesWithProtectedSettings()
    {
        using var state = new PersistenceState();
        var path = state.Temp.CreateDirectory("app_settings.json");
        await Assert.ThrowsAsync<SettingsLoadException>(() => state.Settings.LoadAsync());
        state.CaptureSettings = () => throw new InvalidOperationException("Protected settings must not be captured.");
        var completed = false;

        await state.Coordinator.FlushAsync(() =>
        {
            Assert.Equal(1, state.LibraryWrites);
            completed = true;
            return Task.CompletedTask;
        });

        Assert.True(completed);
        Assert.True(Directory.Exists(path));
        Assert.Empty(state.Errors.Reports);
    }

    private sealed class PersistenceState : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        private readonly EnvironmentVariableScope _environment;
        public SettingsService Settings { get; } = new();
        public RecordingErrorService Errors { get; } = new();
        public IEnumerable<Func<Task>> Notices => Errors.Notifications.OfType<Func<Task>>();
        public DesktopPersistenceCoordinator Coordinator { get; }
        public LibraryChangeTracker Tracker { get; }
        public int LibraryWrites { get; private set; }
        public Exception? LibraryFailure { get; set; }
        public Action? DuringLibraryWrite { get; set; }
        public bool LibraryLoadFailed { get; set; }
        public Func<Task<string>> CaptureSettings { get; set; }

        public PersistenceState()
        {
            _environment = new EnvironmentVariableScope(("APPIMAGE", Temp.GetPath("Retromind.AppImage")));
            CaptureSettings = DefaultSnapshot;
            Tracker = new LibraryChangeTracker(new MediaDataService(), (_, _) => { }, null, null, null, () =>
            {
                LibraryWrites++;
                DuringLibraryWrite?.Invoke();
                return LibraryFailure == null ? Task.CompletedTask : Task.FromException(LibraryFailure);
            });
            Tracker.Initialize(new ObservableCollection<MediaNode>());
            Coordinator = new DesktopPersistenceCoordinator(
                Tracker, Settings, () => LibraryLoadFailed, () => CaptureSettings(), Errors, _ => Task.FromResult(true));
        }

        public void Dirty()
        {
            Call(Tracker, "MarkDirtyCore");
            Call(Tracker, "StopDebouncedSave"); // No asynchronous timer race in these policy tests.
        }

        public Task<(bool AllSaved, bool SettingsSaved)> Save() => Coordinator.SaveAsync();

        public Task<string> DefaultSnapshot() => Task.FromResult(Settings.Serialize(new AppSettings()));

        public void Dispose()
        {
            Coordinator.Dispose();
            Tracker.StopTracking();
            _environment.Dispose();
            Temp.Dispose();
        }
    }

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
