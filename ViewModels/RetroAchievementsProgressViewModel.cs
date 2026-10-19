using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Gog.Achievements;

namespace Retromind.ViewModels;

/// <summary>
/// Shared presentation state for RetroAchievements and Steam achievement progress.
/// The historical type name is retained so external BigMode themes keep working.
/// </summary>
public sealed class RetroAchievementsProgressViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly IRetroAchievementsProgressService _progressService;
    private readonly IRetroAchievementsBadgeService _badgeService;
    private readonly ISteamAchievementsProgressService? _steamProgressService;
    private readonly IGogAchievementsProgressService? _gogProgressService;
    private readonly TimeSpan _selectionDelay;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _badgeLoadCts;
    private MediaItem? _selectedItem;
    private int _selectedGameId;
    private uint _selectedSteamAppId;
    private string? _selectedGogGameId;
    private string? _selectedUserIdentifier;
    private AchievementProvider _activeProvider;
    private bool _isVisible;
    private bool _isLoading;
    private bool _isBadgeLoading;
    private bool _isAchievementsExpanded;
    private string _statusText = string.Empty;
    private RetroAchievementsProgressSnapshot? _snapshot;
    private SteamProgressSnapshot? _steamSnapshot;
    private GogProgressSnapshot? _gogSnapshot;
    private IReadOnlyList<RetroAchievementsAchievementItemViewModel> _achievementItems =
        Array.Empty<RetroAchievementsAchievementItemViewModel>();
    private bool _disposed;

    public RetroAchievementsProgressViewModel(
        AppSettings settings,
        IRetroAchievementsProgressService progressService,
        IRetroAchievementsBadgeService badgeService,
        TimeSpan selectionDelay = default)
        : this(
            settings,
            progressService,
            badgeService,
            steamProgressService: null,
            gogProgressService: null,
            selectionDelay)
    {
    }

    public RetroAchievementsProgressViewModel(
        AppSettings settings,
        IRetroAchievementsProgressService progressService,
        IRetroAchievementsBadgeService badgeService,
        ISteamAchievementsProgressService? steamProgressService,
        TimeSpan selectionDelay = default)
        : this(
            settings,
            progressService,
            badgeService,
            steamProgressService,
            gogProgressService: null,
            selectionDelay)
    {
    }

    public RetroAchievementsProgressViewModel(
        AppSettings settings,
        IRetroAchievementsProgressService progressService,
        IRetroAchievementsBadgeService badgeService,
        ISteamAchievementsProgressService? steamProgressService,
        IGogAchievementsProgressService? gogProgressService,
        TimeSpan selectionDelay = default)
    {
        if (selectionDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(selectionDelay));

        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _progressService = progressService ?? throw new ArgumentNullException(nameof(progressService));
        _badgeService = badgeService ?? throw new ArgumentNullException(nameof(badgeService));
        _steamProgressService = steamProgressService;
        _gogProgressService = gogProgressService;
        _selectionDelay = selectionDelay;
        RefreshCommand = new AsyncRelayCommand(
            () => SelectItemAsync(_selectedItem, forceRefresh: true),
            CanRefresh);
        ToggleAchievementsCommand = new RelayCommand(ToggleAchievements);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ToggleAchievementsCommand { get; }

    public string Title => _activeProvider switch
    {
        AchievementProvider.Steam =>
            T("SteamAchievements_ProgressTitle", "Steam achievement progress"),
        AchievementProvider.Gog =>
            T("GogAchievements_ProgressTitle", "GOG achievement progress"),
        _ => T("RetroAchievements_ProgressTitle", "RetroAchievements progress")
    };
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

    public bool IsBadgeLoading
    {
        get => _isBadgeLoading;
        private set => SetProperty(ref _isBadgeLoading, value);
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
            OnPropertyChanged(nameof(ProviderDetailText));

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

    public bool HasProgress => _activeProvider switch
    {
        AchievementProvider.Steam => _steamSnapshot != null,
        AchievementProvider.Gog => _gogSnapshot != null,
        AchievementProvider.RetroAchievements => Snapshot != null,
        _ => false
    };
    public bool HasAchievements => AchievementItems.Count > 0;
    public bool ShowStatus => !string.IsNullOrWhiteSpace(StatusText);
    public double ProgressMaximum => Math.Max(
        1,
        _activeProvider switch
        {
            AchievementProvider.Steam => _steamSnapshot?.Progress.AchievementCount ?? 1,
            AchievementProvider.Gog => _gogSnapshot?.Progress.AchievementCount ?? 1,
            _ => Snapshot?.Progress.AchievementCount ?? 1
        });
    public double ProgressValue => _activeProvider switch
    {
        AchievementProvider.Steam => _steamSnapshot?.Progress.UnlockedCount ?? 0,
        AchievementProvider.Gog => _gogSnapshot?.Progress.UnlockedCount ?? 0,
        _ => Snapshot?.Progress.AwardedCount ?? 0
    };

    public string SummaryText
    {
        get
        {
            if (_activeProvider == AchievementProvider.Steam)
            {
                var steamProgress = _steamSnapshot?.Progress;
                if (steamProgress == null)
                    return string.Empty;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "SteamAchievements_ProgressSummaryFormat",
                        "{0:N0} of {1:N0} achievements ({2:N1}%)"),
                    steamProgress.UnlockedCount,
                    steamProgress.AchievementCount,
                    steamProgress.CompletionPercent);
            }

            if (_activeProvider == AchievementProvider.Gog)
            {
                var gogProgress = _gogSnapshot?.Progress;
                if (gogProgress == null)
                    return string.Empty;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "GogAchievements_ProgressSummaryFormat",
                        "{0:N0} of {1:N0} achievements ({2:N1}%)"),
                    gogProgress.UnlockedCount,
                    gogProgress.AchievementCount,
                    gogProgress.CompletionPercent);
            }

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
        => ProviderDetailText;

    public string ProviderDetailText
    {
        get
        {
            if (_activeProvider == AchievementProvider.Steam)
            {
                var steamProgress = _steamSnapshot?.Progress;
                if (steamProgress == null || steamProgress.LockedHiddenAchievementCount == 0)
                    return string.Empty;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "SteamAchievements_LockedHiddenSummaryFormat",
                        "Hidden achievements remaining: {0:N0}"),
                    steamProgress.LockedHiddenAchievementCount);
            }

            if (_activeProvider == AchievementProvider.Gog)
            {
                var gogProgress = _gogSnapshot?.Progress;
                if (gogProgress == null || gogProgress.LockedHiddenAchievementCount == 0)
                    return string.Empty;

                return string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "GogAchievements_LockedHiddenSummaryFormat",
                        "Hidden achievements remaining: {0:N0}"),
                    gogProgress.LockedHiddenAchievementCount);
            }

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
        var retroAchievementsVisible =
            retroAchievementsSettings?.Enabled == true && gameId > 0;
        var hasSteamAppId = SteamAppIdResolver.TryGetAppId(item, out var steamAppId);
        var steamAchievementsSettings = _settings.SteamAchievements;
        var steamAchievementsVisible =
            _steamProgressService != null &&
            steamAchievementsSettings?.Enabled == true &&
            hasSteamAppId;
        var gogGameId = GogMediaItemStateHelper.TryGetGameId(item);
        var gogAchievementsVisible =
            _gogProgressService != null &&
            _settings.EnableGogGalaxyIntegration &&
            !string.IsNullOrWhiteSpace(gogGameId);
        var provider = steamAchievementsVisible
            ? AchievementProvider.Steam
            : gogAchievementsVisible
                ? AchievementProvider.Gog
                : retroAchievementsVisible
                    ? AchievementProvider.RetroAchievements
                    : AchievementProvider.None;
        var isVisible = provider != AchievementProvider.None;
        var userIdentifier = provider switch
        {
            AchievementProvider.Steam => steamAchievementsSettings?.SteamId64?.Trim(),
            AchievementProvider.RetroAchievements => FirstNonEmpty(
                retroAchievementsSettings?.UserUlid,
                retroAchievementsSettings?.Username),
            _ => null
        };
        var hasRequestedProgress = provider switch
        {
            AchievementProvider.Steam =>
                _steamSnapshot?.Progress.AppId == steamAppId,
            AchievementProvider.Gog =>
                string.Equals(
                    _gogSnapshot?.Progress.GameId,
                    gogGameId,
                    StringComparison.Ordinal),
            AchievementProvider.RetroAchievements =>
                Snapshot?.Progress.GameId == gameId,
            _ => false
        };
        if (!forceRefresh &&
            ReferenceEquals(_selectedItem, item) &&
            _selectedGameId == gameId &&
            _selectedSteamAppId == steamAppId &&
            string.Equals(_selectedGogGameId, gogGameId, StringComparison.Ordinal) &&
            _activeProvider == provider &&
            IsVisible == isVisible &&
            string.Equals(
                _selectedUserIdentifier,
                userIdentifier,
                StringComparison.OrdinalIgnoreCase) &&
            (IsLoading || hasRequestedProgress))
        {
            return;
        }

        var selectedGameChanged = !ReferenceEquals(_selectedItem, item) ||
                                  _selectedGameId != gameId ||
                                  _selectedSteamAppId != steamAppId ||
                                  !string.Equals(
                                      _selectedGogGameId,
                                      gogGameId,
                                      StringComparison.Ordinal) ||
                                  _activeProvider != provider;
        if (selectedGameChanged)
            IsAchievementsExpanded = false;

        _selectedItem = item;
        _selectedGameId = gameId;
        _selectedSteamAppId = steamAppId;
        _selectedGogGameId = gogGameId;
        _selectedUserIdentifier = userIdentifier;
        CancelCurrentLoad();
        SetActiveProvider(provider);

        IsVisible = isVisible;
        if (selectedGameChanged || !forceRefresh || !IsVisible)
            ClearSnapshots();

        StatusText = string.Empty;
        if (!IsVisible)
        {
            IsLoading = false;
            return;
        }

        var requestCts = new CancellationTokenSource();
        _loadCts = requestCts;
        IsLoading = true;
        StatusText = T(
            "RetroAchievements_ProgressLoading",
            "Loading achievement progress...");

        try
        {
            if (!forceRefresh && _selectionDelay > TimeSpan.Zero)
                await Task.Delay(_selectionDelay, requestCts.Token);

            if (provider == AchievementProvider.Steam)
            {
                var steamSnapshot = await _steamProgressService!
                    .GetProgressAsync(steamAppId, forceRefresh, requestCts.Token);
                if (!IsCurrentRequest(
                        requestCts,
                        item,
                        provider,
                        gameId,
                        steamAppId,
                        gogGameId))
                {
                    return;
                }

                SetSteamSnapshot(steamSnapshot);
                if (IsAchievementsExpanded)
                    StartBadgeLoading(AchievementItems, item, gameId);
                if (steamSnapshot.Progress.AchievementCount == 0)
                {
                    StatusText = T(
                        "SteamAchievements_NoAchievements",
                        "This game has no Steam achievements.");
                }
                else if (steamSnapshot.UsedCachedFallback)
                {
                    StatusText = string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "SteamAchievements_ProgressCachedFallbackFormat",
                            "Steam is unavailable. Showing cached data from {0:g}."),
                        steamSnapshot.FetchedAtUtc.ToLocalTime());
                }
                else
                {
                    StatusText = string.Empty;
                }
            }
            else if (provider == AchievementProvider.Gog)
            {
                var gogSnapshot = await _gogProgressService!
                    .GetProgressAsync(gogGameId!, forceRefresh, requestCts.Token);
                if (!IsCurrentRequest(
                        requestCts,
                        item,
                        provider,
                        gameId,
                        steamAppId,
                        gogGameId))
                {
                    return;
                }

                SetGogSnapshot(gogSnapshot);
                if (IsAchievementsExpanded)
                    StartBadgeLoading(AchievementItems, item, gameId);
                if (gogSnapshot.Progress.AchievementCount == 0)
                {
                    StatusText = T(
                        "GogAchievements_NoAchievements",
                        "This game has no GOG achievements.");
                }
                else if (gogSnapshot.UsedCachedFallback)
                {
                    StatusText = string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "GogAchievements_ProgressCachedFallbackFormat",
                            "GOG is unavailable. Showing cached data from {0:g}."),
                        gogSnapshot.FetchedAtUtc.ToLocalTime());
                }
                else
                {
                    StatusText = string.Empty;
                }
            }
            else
            {
                var snapshot = await _progressService
                    .GetProgressAsync(gameId, forceRefresh, requestCts.Token);
                if (!IsCurrentRequest(
                        requestCts,
                        item,
                        provider,
                        gameId,
                        steamAppId,
                        gogGameId))
                {
                    return;
                }

                Snapshot = snapshot;
                if (IsAchievementsExpanded)
                    StartBadgeLoading(AchievementItems, item, gameId);
                StatusText = snapshot.UsedCachedFallback
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "RetroAchievements_ProgressCachedFallbackFormat",
                            "RetroAchievements is unavailable. Showing cached data from {0:g}."),
                        snapshot.FetchedAtUtc.ToLocalTime())
                    : string.Empty;
            }
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Selection changed or the view model is being disposed.
        }
        catch (Exception ex) when (ex is RetroAchievementsApiException or
                                      SteamApiException or
                                      GogAchievementsApiException or
                                      InvalidOperationException)
        {
            if (!IsCurrentRequest(
                    requestCts,
                    item,
                    provider,
                    gameId,
                    steamAppId,
                    gogGameId))
                return;

            Debug.WriteLine($"[Achievements] Could not load progress: {ex.Message}");
            StatusText = T(
                "RetroAchievements_ProgressLoadFailed",
                "Achievement progress could not be loaded.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(
                    requestCts,
                    item,
                    provider,
                    gameId,
                    steamAppId,
                    gogGameId))
                return;

            Debug.WriteLine($"[Achievements] Unexpected progress load failure: {ex}");
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
        IsVisible &&
        !IsLoading &&
        (_activeProvider switch
        {
            AchievementProvider.Steam =>
                SteamAppIdResolver.TryGetAppId(_selectedItem, out _),
            AchievementProvider.Gog =>
                GogMediaItemStateHelper.TryGetGameId(_selectedItem) != null,
            AchievementProvider.RetroAchievements =>
                (_selectedItem?.RetroAchievementsGame?.GameId ?? 0) > 0,
            _ => false
        });

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
        if (!HasAchievements)
            return;

        IsAchievementsExpanded = !IsAchievementsExpanded;
        if (IsAchievementsExpanded)
            EnsureAchievementBadgesLoaded();
    }

    public void EnsureAchievementBadgesLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasAchievements)
            StartBadgeLoading(AchievementItems, _selectedItem, _selectedGameId);
    }

    private void SetActiveProvider(AchievementProvider provider)
    {
        if (_activeProvider == provider)
            return;

        _activeProvider = provider;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HardcoreSummaryText));
        OnPropertyChanged(nameof(ProviderDetailText));
    }

    private void ClearSnapshots()
    {
        Snapshot = null;
        SetSteamSnapshot(null);
        SetGogSnapshot(null);
        AchievementItems = Array.Empty<RetroAchievementsAchievementItemViewModel>();
    }

    private void SetSteamSnapshot(SteamProgressSnapshot? value)
    {
        if (Equals(_steamSnapshot, value))
            return;

        _steamSnapshot = value;
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HardcoreSummaryText));
        OnPropertyChanged(nameof(ProviderDetailText));

        if (_activeProvider != AchievementProvider.Steam)
            return;

        AchievementItems = value?.Progress.Achievements
            .Select(achievement => new RetroAchievementsAchievementItemViewModel(achievement))
            .ToArray()
            ?? Array.Empty<RetroAchievementsAchievementItemViewModel>();
    }

    private void SetGogSnapshot(GogProgressSnapshot? value)
    {
        if (Equals(_gogSnapshot, value))
            return;

        _gogSnapshot = value;
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HardcoreSummaryText));
        OnPropertyChanged(nameof(ProviderDetailText));

        if (_activeProvider != AchievementProvider.Gog)
            return;

        AchievementItems = value?.Progress.Achievements
            .OrderByDescending(achievement => achievement.IsUnlocked)
            .ThenByDescending(achievement => achievement.UnlockedAtUtc)
            .ThenBy(achievement => achievement.Rarity)
            .ThenBy(achievement => achievement.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(achievement => new RetroAchievementsAchievementItemViewModel(achievement))
            .ToArray()
            ?? Array.Empty<RetroAchievementsAchievementItemViewModel>();
    }

    private bool IsCurrentRequest(
        CancellationTokenSource requestCts,
        MediaItem? item,
        AchievementProvider provider,
        int gameId,
        uint steamAppId,
        string? gogGameId)
    {
        return ReferenceEquals(_loadCts, requestCts) &&
               ReferenceEquals(_selectedItem, item) &&
               _selectedGameId == gameId &&
               _selectedSteamAppId == steamAppId &&
               string.Equals(_selectedGogGameId, gogGameId, StringComparison.Ordinal) &&
               _activeProvider == provider &&
               (provider switch
               {
                   AchievementProvider.Steam =>
                       SteamAppIdResolver.TryGetAppId(item, out var currentAppId) &&
                       currentAppId == steamAppId,
                   AchievementProvider.Gog =>
                       string.Equals(
                           GogMediaItemStateHelper.TryGetGameId(item),
                           gogGameId,
                           StringComparison.Ordinal),
                   AchievementProvider.RetroAchievements =>
                       item?.RetroAchievementsGame?.GameId == gameId,
                   _ => false
               });
    }

    private void CancelCurrentLoad()
    {
        var previous = _loadCts;
        _loadCts = null;
        previous?.Cancel();
        previous?.Dispose();

        var previousBadgeLoad = _badgeLoadCts;
        _badgeLoadCts = null;
        IsBadgeLoading = false;
        previousBadgeLoad?.Cancel();
    }

    private void StartBadgeLoading(
        IReadOnlyList<RetroAchievementsAchievementItemViewModel> achievements,
        MediaItem? item,
        int gameId)
    {
        if (achievements.Count == 0 ||
            _badgeLoadCts != null ||
            !ReferenceEquals(_selectedItem, item) ||
            _selectedGameId != gameId)
        {
            return;
        }

        if (_activeProvider is AchievementProvider.Steam or AchievementProvider.Gog)
        {
            if (_activeProvider == AchievementProvider.Steam &&
                (!SteamAppIdResolver.TryGetAppId(item, out var appId) ||
                 appId != _selectedSteamAppId))
            {
                return;
            }

            if (_activeProvider == AchievementProvider.Gog &&
                !string.Equals(
                    GogMediaItemStateHelper.TryGetGameId(item),
                    _selectedGogGameId,
                    StringComparison.Ordinal))
            {
                return;
            }

            foreach (var achievement in achievements)
                achievement.SetBadgePath(achievement.ImageReference);
            return;
        }

        if (item?.RetroAchievementsGame?.GameId != gameId)
            return;

        var missingBadges = achievements
            .Where(achievement => string.IsNullOrWhiteSpace(achievement.BadgePath))
            .ToArray();
        if (missingBadges.Length == 0)
            return;

        var badgeLoadCts = new CancellationTokenSource();
        _badgeLoadCts = badgeLoadCts;
        IsBadgeLoading = true;
        _ = LoadBadgesAsync(missingBadges, badgeLoadCts, item, gameId);
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
            {
                _badgeLoadCts = null;
                IsBadgeLoading = false;
            }

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

    private enum AchievementProvider
    {
        None,
        RetroAchievements,
        Steam,
        Gog
    }
}
