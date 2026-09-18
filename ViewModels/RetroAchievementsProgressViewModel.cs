using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
    private readonly IRetroAchievementsBadgeService _badgeService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _badgeLoadCts;
    private MediaItem? _selectedItem;
    private int _selectedGameId;
    private string? _selectedUserIdentifier;
    private bool _isVisible;
    private bool _isLoading;
    private bool _isAchievementsExpanded;
    private string _statusText = string.Empty;
    private RetroAchievementsProgressSnapshot? _snapshot;
    private IReadOnlyList<RetroAchievementsAchievementItemViewModel> _achievementItems =
        Array.Empty<RetroAchievementsAchievementItemViewModel>();
    private bool _disposed;

    public RetroAchievementsProgressViewModel(
        AppSettings settings,
        IRetroAchievementsProgressService progressService,
        IRetroAchievementsBadgeService badgeService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _progressService = progressService ?? throw new ArgumentNullException(nameof(progressService));
        _badgeService = badgeService ?? throw new ArgumentNullException(nameof(badgeService));
        RefreshCommand = new AsyncRelayCommand(
            () => SelectItemAsync(_selectedItem, forceRefresh: true),
            CanRefresh);
        ToggleAchievementsCommand = new RelayCommand(ToggleAchievements);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ToggleAchievementsCommand { get; }

    public string Title => T("RetroAchievements_ProgressTitle", "RetroAchievements progress");
    public string RefreshText => T("RetroAchievements_ProgressRefresh", "Refresh");
    public string AchievementsTitle => T("RetroAchievements_AchievementsTitle", "Achievements");
    public string AchievementsHeaderText => string.Format(
        CultureInfo.CurrentCulture,
        "{0} ({1:N0})",
        AchievementsTitle,
        AchievementItems.Count);
    public string AchievementsToggleGlyph => IsAchievementsExpanded ? "▾" : "▸";

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

    public bool IsAchievementsExpanded
    {
        get => _isAchievementsExpanded;
        private set
        {
            if (!SetProperty(ref _isAchievementsExpanded, value))
                return;

            OnPropertyChanged(nameof(AchievementsToggleGlyph));
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

            AchievementItems = value?.Progress.Achievements
                .OrderBy(achievement => achievement.DisplayOrder)
                .ThenBy(achievement => achievement.AchievementId)
                .Select(achievement => new RetroAchievementsAchievementItemViewModel(achievement))
                .ToArray()
                ?? Array.Empty<RetroAchievementsAchievementItemViewModel>();
        }
    }

    public IReadOnlyList<RetroAchievementsAchievementItemViewModel> AchievementItems
    {
        get => _achievementItems;
        private set
        {
            if (SetProperty(ref _achievementItems, value))
            {
                OnPropertyChanged(nameof(HasAchievements));
                OnPropertyChanged(nameof(AchievementsHeaderText));
            }
        }
    }

    public bool HasProgress => Snapshot != null;
    public bool HasAchievements => AchievementItems.Count > 0;
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

        var gameId = item?.RetroAchievementsGame?.GameId ?? 0;
        var retroAchievementsSettings = _settings.RetroAchievements;
        var isVisible = retroAchievementsSettings?.Enabled == true && gameId > 0;
        var userIdentifier = FirstNonEmpty(
            retroAchievementsSettings?.UserUlid,
            retroAchievementsSettings?.Username);
        if (!forceRefresh &&
            ReferenceEquals(_selectedItem, item) &&
            _selectedGameId == gameId &&
            IsVisible == isVisible &&
            string.Equals(
                _selectedUserIdentifier,
                userIdentifier,
                StringComparison.OrdinalIgnoreCase) &&
            (IsLoading || Snapshot?.Progress.GameId == gameId))
        {
            return;
        }

        var selectedGameChanged = !ReferenceEquals(_selectedItem, item) ||
                                  _selectedGameId != gameId;
        if (selectedGameChanged)
            IsAchievementsExpanded = false;

        _selectedItem = item;
        _selectedGameId = gameId;
        _selectedUserIdentifier = userIdentifier;
        CancelCurrentLoad();

        IsVisible = isVisible;
        if (!forceRefresh || !IsVisible)
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
            StartBadgeLoading(AchievementItems, requestCts, item, gameId);
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private void ToggleAchievements()
    {
        if (HasAchievements)
            IsAchievementsExpanded = !IsAchievementsExpanded;
    }

    private bool IsCurrentRequest(
        CancellationTokenSource requestCts,
        MediaItem? item,
        int gameId)
    {
        return ReferenceEquals(_loadCts, requestCts) &&
               ReferenceEquals(_selectedItem, item) &&
               _selectedGameId == gameId &&
               item?.RetroAchievementsGame?.GameId == gameId;
    }

    private void CancelCurrentLoad()
    {
        var previous = _loadCts;
        _loadCts = null;
        previous?.Cancel();
        previous?.Dispose();

        var previousBadgeLoad = _badgeLoadCts;
        _badgeLoadCts = null;
        previousBadgeLoad?.Cancel();
    }

    private void StartBadgeLoading(
        IReadOnlyList<RetroAchievementsAchievementItemViewModel> achievements,
        CancellationTokenSource progressRequest,
        MediaItem? item,
        int gameId)
    {
        if (achievements.Count == 0 ||
            !IsCurrentRequest(progressRequest, item, gameId))
        {
            return;
        }

        var badgeLoadCts = new CancellationTokenSource();
        _badgeLoadCts = badgeLoadCts;
        _ = LoadBadgesAsync(achievements, badgeLoadCts, item, gameId);
    }

    private async Task LoadBadgesAsync(
        IReadOnlyList<RetroAchievementsAchievementItemViewModel> achievements,
        CancellationTokenSource requestCts,
        MediaItem? item,
        int gameId)
    {
        using var concurrencyGate = new SemaphoreSlim(4, 4);
        try
        {
            var loads = achievements.Select(achievement =>
                LoadBadgeAsync(
                    achievement,
                    concurrencyGate,
                    requestCts,
                    item,
                    gameId));
            await Task.WhenAll(loads);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // The selected game changed or the view model is being disposed.
        }
        finally
        {
            if (ReferenceEquals(_badgeLoadCts, requestCts))
                _badgeLoadCts = null;

            requestCts.Dispose();
        }
    }

    private async Task LoadBadgeAsync(
        RetroAchievementsAchievementItemViewModel achievement,
        SemaphoreSlim concurrencyGate,
        CancellationTokenSource requestCts,
        MediaItem? item,
        int gameId)
    {
        var lockTaken = false;
        try
        {
            await concurrencyGate.WaitAsync(requestCts.Token);
            lockTaken = true;
            var badgePath = await _badgeService.GetBadgePathAsync(
                achievement.BadgeName,
                achievement.IsUnlocked,
                requestCts.Token);
            if (ReferenceEquals(_badgeLoadCts, requestCts) &&
                ReferenceEquals(_selectedItem, item) &&
                _selectedGameId == gameId)
            {
                achievement.SetBadgePath(badgePath);
            }
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Badge images are optional and selection changes cancel them routinely.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[RetroAchievements] Could not load badge '{achievement.BadgeName}': {ex.Message}");
        }
        finally
        {
            if (lockTaken)
                concurrencyGate.Release();
        }
    }
}
