using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

internal sealed class DesktopPersistenceCoordinator : IDisposable
{
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(500);

    internal static string OperationSavePendingMessage() => ViewModelBase.T(
        "Persistence.OperationSavePending",
        "The operation completed, but its changes could not be fully saved. Changes remain in memory; completed work was retained.");

    private readonly LibraryChangeTracker _libraryTracker;
    private readonly SettingsService _settingsService;
    private readonly Func<bool> _libraryLoadFailed;
    private readonly Func<Task<string>> _captureSettings;
    private readonly IErrorService _errorService;
    private readonly Func<string, Task<bool>> _tryShowSaveFailure;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private CancellationTokenSource? _settingsSaveCancellation;
    private int _settingsDirtyVersion;
    private int _settingsSaveFailureVersion;
    private bool _librarySaveFailed;
    private bool _settingsSaveFailed;
    private int _saveFailureNoticeShown;
    private int _suppressSaveFailureNotice;

    internal DesktopPersistenceCoordinator(
        LibraryChangeTracker libraryTracker,
        SettingsService settingsService,
        Func<bool> libraryLoadFailed,
        Func<Task<string>> captureSettings,
        IErrorService errorService,
        Func<string, Task<bool>> tryShowSaveFailure)
    {
        _libraryTracker = libraryTracker;
        _settingsService = settingsService;
        _libraryLoadFailed = libraryLoadFailed;
        _captureSettings = captureSettings;
        _errorService = errorService;
        _tryShowSaveFailure = tryShowSaveFailure;

        _libraryTracker.SaveFailed += OnLibrarySaveFailed;
        _libraryTracker.SaveSucceeded += OnLibrarySaveSucceeded;
    }

    // Settings credential changes depend on the actual settings write, even during a partial save.
    internal async Task<(bool AllSaved, bool SettingsSaved)> SaveAsync()
    {
        var librarySaved = false;
        if (!_libraryLoadFailed() && _libraryTracker.IsTracking)
        {
            try
            {
                await _libraryTracker.SaveIfDirtyAsync(force: false).ConfigureAwait(false);
                librarySaved = _libraryTracker.IsTracking && !_libraryTracker.IsDirty;
            }
            catch
            {
                // The tracker retains dirty state and its SaveFailed event owns the report.
                return (false, false);
            }
        }

        if (_settingsService.HasLoadFailure)
            return (false, false);

        try
        {
            var saveVersion = Volatile.Read(ref _settingsDirtyVersion);
            await SaveSettingsSnapshotAsync(saveVersion).ConfigureAwait(false);
            lock (_stateLock)
                return (librarySaved && !_libraryLoadFailed() && _libraryTracker.IsTracking && !_libraryTracker.IsDirty &&
                        !_librarySaveFailed && !_settingsSaveFailed &&
                        Volatile.Read(ref _settingsDirtyVersion) == saveVersion, true);
        }
        catch (OperationCanceledException)
        {
            // Restore deliberately rejects regular settings writes.
            return (false, false);
        }
        catch (Exception ex)
        {
            OnSettingsSaveFailed(ex);
            return (false, false);
        }
    }

    internal Task ScheduleSettingsSave()
    {
        if (_settingsService.HasLoadFailure)
            return Task.CompletedTask;

        var saveVersion = Interlocked.Increment(ref _settingsDirtyVersion);
        CancelPendingSettingsSave();
        _settingsSaveCancellation?.Dispose();
        _settingsSaveCancellation = new CancellationTokenSource();
        var token = _settingsSaveCancellation.Token;

        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingsSaveDelay, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await SaveSettingsSnapshotAsync(saveVersion, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A newer edit, restore, or shutdown can cancel the debounce.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Debounced save failed: {ex.Message}");
                OnSettingsSaveFailed(ex);
            }
        }, token);
    }

    internal void CancelPendingSettingsSave() => _settingsSaveCancellation?.Cancel();

    // Keep notices suppressed until the caller has finished closing its desktop session.
    internal async Task FlushAsync(Func<Task> onSaved)
    {
        Interlocked.Exchange(ref _suppressSaveFailureNotice, 1);
        try
        {
            if (!_libraryLoadFailed())
                await _libraryTracker.SaveIfDirtyAsync(force: true).ConfigureAwait(false);

            if (!_settingsService.HasLoadFailure)
                await SaveSettingsSnapshotAsync(Volatile.Read(ref _settingsDirtyVersion)).ConfigureAwait(false);

            lock (_stateLock) TryClearSaveFailureNotice();
            await onSaved().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _suppressSaveFailureNotice, 0);
        }
    }

    private async Task SaveSettingsSnapshotAsync(int saveVersion, CancellationToken token = default)
    {
        // Serialize snapshot capture as well as writing to prevent stale snapshots from overtaking newer saves.
        await _settingsSaveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            int failureVersion;
            lock (_stateLock) failureVersion = _settingsSaveFailureVersion;
            var json = await _captureSettings().ConfigureAwait(false);
            await _settingsService.SaveJsonAsync(json, token).ConfigureAwait(false);
            lock (_stateLock)
            {
                if (Volatile.Read(ref _settingsDirtyVersion) == saveVersion && failureVersion == _settingsSaveFailureVersion)
                {
                    _settingsSaveFailed = false;
                    TryClearSaveFailureNotice();
                }
            }
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void OnLibrarySaveFailed(Exception exception)
    {
        lock (_stateLock) _librarySaveFailed = true;
        if (exception is not OperationCanceledException)
            ReportSaveFailure(exception);
    }

    private void OnLibrarySaveSucceeded()
    {
        lock (_stateLock)
        {
            if (!_libraryTracker.IsDirty)
                _librarySaveFailed = false;
            TryClearSaveFailureNotice();
        }
    }

    private void OnSettingsSaveFailed(Exception exception)
    {
        lock (_stateLock)
        {
            _settingsSaveFailed = true;
            _settingsSaveFailureVersion++;
        }
        ReportSaveFailure(exception);
    }

    // Called under _stateLock. A successful write only resolves its own store's failure.
    private void TryClearSaveFailureNotice()
    {
        if (!_librarySaveFailed && !_settingsSaveFailed && !_libraryLoadFailed() && !_settingsService.HasLoadFailure &&
            _libraryTracker.IsTracking && !_libraryTracker.IsDirty)
            ResetSaveFailureNotice();
    }

    private void ReportSaveFailure(Exception exception)
    {
        var showNotice = Volatile.Read(ref _suppressSaveFailureNotice) == 0 &&
                         Interlocked.CompareExchange(ref _saveFailureNoticeShown, 1, 0) == 0;
        var format = Strings.ResourceManager.GetString("Persistence.SaveFailedFormat", Strings.Culture);
        if (string.IsNullOrWhiteSpace(format))
            format = "Retromind could not save its data.\n\n{0}\n\nUnsaved changes remain in memory. Please check free disk space and write permissions.";
        var message = string.Format(format, exception.Message);

        _errorService.Report(new ErrorReport(
            "Persistence.SaveFailed",
            ErrorSeverity.Error,
            nameof(MainWindowViewModel),
            message,
            exception,
            Context: new Dictionary<string, string> { ["Operation"] = "Save" }),
            notifyUser: showNotice ? () => ShowSaveFailureAsync(message) : null);
    }

    private async Task ShowSaveFailureAsync(string message)
    {
        try
        {
            if (await _tryShowSaveFailure(message))
                return;
        }
        catch (Exception dialogException)
        {
            Debug.WriteLine($"[Persistence] Could not show save failure: {dialogException.Message}");
        }

        ResetSaveFailureNotice();
    }

    private void ResetSaveFailureNotice() => Interlocked.Exchange(ref _saveFailureNoticeShown, 0);

    public void Dispose()
    {
        _libraryTracker.SaveFailed -= OnLibrarySaveFailed;
        _libraryTracker.SaveSucceeded -= OnLibrarySaveSucceeded;
        CancelPendingSettingsSave();
        _settingsSaveCancellation?.Dispose();
        _settingsSaveCancellation = null;
    }
}
