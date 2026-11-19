using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class MainWindowViewModelPersistenceErrorTests
{
    [Fact]
    public async Task SaveFailures_ReportEachAttemptAndKeepTheExistingLocalizedNotice()
    {
        var errors = new RecordingErrorService();
        var failure = new IOException("Disk full");
        using var persistence = CreateFailingSettingsCoordinator(errors, failure);

        await persistence.SaveAsync();
        await persistence.SaveAsync();

        Assert.Equal(2, errors.Reports.Count);
        Assert.Single(errors.Notifications.OfType<Func<Task>>());
        var report = errors.Reports.First();
        Assert.Equal("Persistence.SaveFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal(nameof(MainWindowViewModel), report.Component);
        Assert.Equal("Save", report.Context!["Operation"]);
        Assert.Same(failure, report.Exception);
        var format = Strings.ResourceManager.GetString("Persistence.SaveFailedFormat", Strings.Culture)!;
        Assert.Equal(string.Format(format, failure.Message), report.Message);
    }

    [Fact]
    public async Task ReportPersistenceSaveFailure_ConcurrentFailuresRequestOnlyOneNotice()
    {
        var errors = new RecordingErrorService();
        using var persistence = CreateFailingSettingsCoordinator(errors, new IOException("Disk full"));
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(persistence.SaveAsync)));

        Assert.Equal(20, errors.Reports.Count);
        Assert.Single(errors.Notifications.OfType<Func<Task>>());
    }

    [Fact]
    public async Task ReportPersistenceSaveFailure_MissingOwnerReleasesNoticeForLaterFailure()
    {
        var errors = new RecordingErrorService();
        using var persistence = CreateFailingSettingsCoordinator(errors, new IOException("Disk full"));
        await persistence.SaveAsync();
        await Assert.Single(errors.Notifications.OfType<Func<Task>>())();
        await persistence.SaveAsync();
        Assert.Equal(2, errors.Notifications.OfType<Func<Task>>().Count());
    }

    [Fact]
    public async Task Flush_FailureIsReportedWithoutConsumingNoticeGate()
    {
        var errors = new RecordingErrorService();
        var failure = new IOException("Final save failed");
        var tracker = CreateFailingLibraryTracker(failure);
        using var persistence = new DesktopPersistenceCoordinator(tracker, new SettingsService(), () => false,
            () => throw new InvalidOperationException("The library failure must stop the flush."),
            errors, _ => Task.FromResult(false));
        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => persistence.FlushAsync(
                () => throw new InvalidOperationException("Failed flush must not run cleanup.")));
            Assert.Same(failure, exception);
            Assert.Single(errors.Reports);
            Assert.Empty(errors.Notifications.OfType<Func<Task>>());

            Assert.Equal((false, false), await persistence.SaveAsync());
            Assert.Single(errors.Notifications.OfType<Func<Task>>());
        }
        finally
        {
            tracker.StopTracking();
        }
    }

    [Fact]
    public async Task FlushAndCleanupAsync_FailedFinalLibrarySaveKeepsDirtyStateAndAllowsCancelledExit()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        var failure = new IOException("Final save failed");
        var tracker = CreateFailingLibraryTracker(failure);
        using var persistence = new DesktopPersistenceCoordinator(tracker, new SettingsService(), () => false,
            () => throw new InvalidOperationException("The library failure must stop the flush."),
            errors, _ => Task.FromResult(false));
        var launcher = new LauncherService(temp.RootPath, new AppSettings(),
            new LaunchLogService(temp.GetPath("Logs", "Launch")));
        SetField(vm, "_audioService", new AudioService(errors));
        SetField(vm, "_launcherService", launcher);
        SetField(vm, "_libraryTracker", tracker);
        SetField(vm, "_persistence", persistence);

        try
        {
            Assert.False(await vm.FlushAndCleanupAsync());
            Assert.True(tracker.IsDirty);
            Assert.Equal(0, GetField<int>(vm, "_cleanupOnce"));
            Assert.Equal(0, GetField<int>(launcher, "_shutdownRequested"));
            Assert.Single(errors.Reports);
            Assert.Empty(errors.Notifications.OfType<Func<Task>>());

            // Declining exit can be followed by another final-save attempt; cleanup never ran.
            Assert.False(await vm.FlushAndCleanupAsync());
            Assert.Equal(2, errors.Reports.Count);
            Assert.Empty(errors.Notifications.OfType<Func<Task>>());
            Assert.False(await vm.SaveData());
            Assert.Single(errors.Notifications.OfType<Func<Task>>());
        }
        finally
        {
            tracker.StopTracking();
        }
    }

    private static DesktopPersistenceCoordinator CreateFailingSettingsCoordinator(IErrorService errors, Exception failure)
    {
        var tracker = new LibraryChangeTracker(new MediaDataService(), (_, _) => { });
        return new DesktopPersistenceCoordinator(tracker, new SettingsService(), () => false,
            () => Task.FromException<string>(failure), errors, _ => Task.FromResult(false));
    }

    private static LibraryChangeTracker CreateFailingLibraryTracker(Exception failure)
    {
        var tracker = new LibraryChangeTracker(new MediaDataService(), (_, _) => { },
            null, null, null, () => Task.FromException(failure));
        tracker.Initialize(new ObservableCollection<MediaNode>());
        return tracker;
    }

    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T GetField<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

}
