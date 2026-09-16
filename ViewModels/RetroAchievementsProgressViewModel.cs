using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services.RetroAchievements;

namespace Retromind.ViewModels;

/// <summary>
/// Presentation state for the RetroAchievements summary in the desktop media details.
/// </summary>
public sealed class RetroAchievementsProgressViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IRetroAchievementsProgressService _progressService;
    private CancellationTokenSource? _loadCts;
    private MediaItem? _selectedItem;
    private bool _isVisible;
    private bool _isLoading;
    private string _statusText = string.Empty;
    private RetroAchievementsProgressSnapshot? _snapshot;
    private bool _disposed;

    public RetroAchievementsProgressViewModel(
        AppSettings settings,
        IRetroAchievementsProgressService progressService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _progressService = progressService ?? throw new ArgumentNullException(nameof(progressService));
        RefreshCommand = new AsyncRelayCommand(
            () => SelectItemAsync(_selectedItem, forceRefresh: true),
            CanRefresh);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public string Title => T("RetroAchievements_ProgressTitle", "RetroAchievements progress");
    public string RefreshText => T("RetroAchievements_ProgressRefresh", "Refresh");

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (SetProperty(ref _isVisible, value))
                RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(ShowStatus));
        }
    }

    public RetroAchievementsProgressSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!SetProperty(ref _snapshot, value))
                return;

            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(ProgressMaximum));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(HardcoreSummaryText));
        }
    }

    public bool HasProgress => Snapshot != null;
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusText);
    public double ProgressMaximum => Math.Max(1, Snapshot?.Progress.AchievementCount ?? 1);
    public double ProgressValue => Snapshot?.Progress.AwardedCount ?? 0;

    public string SummaryText
    {
        get
        {
            var progress = Snapshot?.Progress;
            if (progress == null)
                return string.Empty;

            return string.Format(
                CultureInfo.CurrentCulture,
                T(
                    "RetroAchievements_ProgressSummaryFormat",
                    "{0:N0} of {1:N0} achievements ({2:N1}%)"),
                progress.AwardedCount,
                progress.AchievementCount,
                progress.CompletionPercent);
        }
    }

    public string HardcoreSummaryText
    {
        get
        {
            var progress = Snapshot?.Progress;
            if (progress == null)
                return string.Empty;

            return string.Format(
                CultureInfo.CurrentCulture,
                T(
                    "RetroAchievements_ProgressHardcoreFormat",
                    "Hardcore: {0:N0} of {1:N0} ({2:N1}%)"),
                progress.AwardedHardcoreCount,
                progress.AchievementCount,
                progress.CompletionHardcorePercent);
        }
    }

    public async Task SelectItemAsync(
        MediaItem? item,
        bool forceRefresh = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _selectedItem = item;
        CancelCurrentLoad();

        var gameId = item?.RetroAchievementsGame?.GameId ?? 0;
        IsVisible = _settings.RetroAchievements?.Enabled == true && gameId > 0;
        if (!forceRefresh)
            Snapshot = null;

        StatusText = string.Empty;
        if (!IsVisible)
        {
            IsLoading = false;
            return;
        }

        var requestCts = new CancellationTokenSource();
        _loadCts = requestCts;
        IsLoading = true;
        StatusText = T("RetroAchievements_ProgressLoading", "Loading achievement progress...");

        try
        {
            var snapshot = await _progressService
                .GetProgressAsync(gameId, forceRefresh, requestCts.Token);
            if (!IsCurrentRequest(requestCts, item, gameId))
                return;

            Snapshot = snapshot;
            StatusText = snapshot.UsedCachedFallback
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "RetroAchievements_ProgressCachedFallbackFormat",
                        "RetroAchievements is unavailable. Showing cached data from {0:g}."),
                    snapshot.FetchedAtUtc.ToLocalTime())
                : string.Empty;
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Selection changed or the view model is being disposed.
        }
        catch (Exception ex) when (ex is RetroAchievementsApiException or InvalidOperationException)
        {
            if (!IsCurrentRequest(requestCts, item, gameId))
                return;

            Debug.WriteLine($"[RetroAchievements] Could not load progress: {ex.Message}");
            StatusText = T(
                "RetroAchievements_ProgressLoadFailed",
                "Achievement progress could not be loaded.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(requestCts, item, gameId))
                return;

            Debug.WriteLine($"[RetroAchievements] Unexpected progress load failure: {ex}");
            StatusText = T(
                "RetroAchievements_ProgressLoadFailed",
                "Achievement progress could not be loaded.");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, requestCts))
            {
                _loadCts = null;
                IsLoading = false;
            }

            requestCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelCurrentLoad();
        GC.SuppressFinalize(this);
    }

    private bool CanRefresh() =>
        IsVisible && !IsLoading && (_selectedItem?.RetroAchievementsGame?.GameId ?? 0) > 0;

    private bool IsCurrentRequest(
        CancellationTokenSource requestCts,
        MediaItem? item,
        int gameId)
    {
        return ReferenceEquals(_loadCts, requestCts) &&
               ReferenceEquals(_selectedItem, item) &&
               item?.RetroAchievementsGame?.GameId == gameId;
    }

    private void CancelCurrentLoad()
    {
        var previous = _loadCts;
        _loadCts = null;
        previous?.Cancel();
        previous?.Dispose();
    }
}
