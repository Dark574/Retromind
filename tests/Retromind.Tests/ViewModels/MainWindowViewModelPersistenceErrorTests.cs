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
    public void ReportPersistenceSaveFailure_ReportsEachFailureButRequestsOnlyOneNoticeUntilSuccessfulSave()
    {
        var errors = new RecordingErrorService();
        var vm = CreatePersistenceShell(errors);
        var failure = new IOException("Disk full");

        Report(vm, failure);
        Report(vm, failure); // A separate failed attempt is diagnosed, but does not open another notice.

        Assert.Equal(2, errors.Reports.Count);
        Assert.Single(errors.Notifications);
        var report = errors.Reports[0];
        Assert.Equal("Persistence.SaveFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Same(failure, report.Exception);
        var format = Strings.ResourceManager.GetString("Persistence.SaveFailedFormat", Strings.Culture)!;
        Assert.Equal(string.Format(format, failure.Message), report.Message);

        Invoke(vm, "ResetPersistenceErrorNotice");
        Report(vm, failure);
        Assert.Equal(2, errors.Notifications.Count);
    }

    [Fact]
    public async Task ReportPersistenceSaveFailure_ConcurrentFailuresRequestOnlyOneNotice()
    {
        var errors = new RecordingErrorService();
        var vm = CreatePersistenceShell(errors);
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => Report(vm, new IOException("Disk full")))));

        Assert.Equal(20, errors.Reports.Count);
        Assert.Single(errors.Notifications);
    }

    [Fact]
    public async Task ReportPersistenceSaveFailure_MissingOwnerReleasesNoticeForLaterFailure()
    {
        var errors = new RecordingErrorService();
        var vm = CreatePersistenceShell(errors);
        Report(vm, new IOException("Disk full"));
        // No Avalonia window is created in this test; the existing callback must reset its gate.
        await errors.Notifications[0]();
        Report(vm, new IOException("Disk full"));
        Assert.Equal(2, errors.Notifications.Count);
    }

    [Fact]
    public void ReportPersistenceSaveFailure_ShutdownSuppressionStillLogsWithoutConsumingNoticeGate()
    {
        var errors = new RecordingErrorService();
        var vm = CreatePersistenceShell(errors);
        SetField(vm, "_suppressPersistenceErrorNotice", 1);
        Report(vm, new IOException("Final save failed"));
        Assert.Single(errors.Reports);
        Assert.Empty(errors.Notifications);
        Assert.Equal(0, GetField<int>(vm, "_persistenceErrorNoticeShown"));
    }

    [Fact]
    public async Task FlushAndCleanupAsync_FailedFinalLibrarySaveKeepsDirtyStateAndAllowsCancelledExit()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var vm = CreatePersistenceShell(errors);
        var failure = new IOException("Final save failed");
        var tracker = new LibraryChangeTracker(new MediaDataService(), (_, _) => { },
            null, null, null, () => Task.FromException(failure));
        tracker.Initialize(new ObservableCollection<MediaNode>());
        tracker.SaveFailed += exception => Report(vm, exception);
        var launcher = new LauncherService(temp.RootPath, new AppSettings(),
            new LaunchLogService(temp.GetPath("Logs", "Launch")));
        SetField(vm, "_audioService", new AudioService(errors));
        SetField(vm, "_launcherService", launcher);
        SetField(vm, "_libraryTracker", tracker);

        try
        {
            Assert.False(await vm.FlushAndCleanupAsync());
            Assert.True(tracker.IsDirty);
            Assert.Equal(0, GetField<int>(vm, "_cleanupOnce"));
            Assert.Equal(0, GetField<int>(vm, "_suppressPersistenceErrorNotice"));
            Assert.Equal(0, GetField<int>(launcher, "_shutdownRequested"));
            Assert.Single(errors.Reports);
            Assert.Empty(errors.Notifications);

            // Declining exit can be followed by another final-save attempt; cleanup never ran.
            Assert.False(await vm.FlushAndCleanupAsync());
            Assert.Equal(2, errors.Reports.Count);
            Assert.Empty(errors.Notifications);
            Report(vm, failure);
            Assert.Single(errors.Notifications);
        }
        finally
        {
            tracker.StopTracking();
        }
    }

    // Exercise the actual persistence methods without starting SDL, audio, account providers,
    // or a desktop window through the full application's constructor. Only fields used by
    // these paths are populated; no production constructors or service boundaries are widened.
    private static MainWindowViewModel CreatePersistenceShell(IErrorService errors)
    {
        var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        SetField(vm, "_errorService", errors);
        SetField(vm, "_persistenceStateLock", new object());
        return vm;
    }

    private static void Report(MainWindowViewModel vm, Exception exception) =>
        Invoke(vm, "ReportPersistenceSaveFailure", exception);

    private static void Invoke(object target, string method, params object[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T GetField<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class RecordingErrorService : IErrorService
    {
        public List<ErrorReport> Reports { get; } = [];
        public List<Func<Task>> Notifications { get; } = [];

        public void Report(ErrorReport error, Func<Task>? notifyUser = null)
        {
            lock (Reports)
            {
                Reports.Add(error);
                if (notifyUser != null)
                    Notifications.Add(notifyUser);
            }
        }
    }
}
