using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;
using Retromind.Services;
using Retromind.Services.BigMode;

namespace Retromind.ViewModels;

internal enum BigModeInputContext { Library, Home, Achievements, Alphabet, Search, SystemMenu }

public partial class BigModeViewModel
{
    internal BigModeInputContext InputContext => IsSystemMenuOpen ? BigModeInputContext.SystemMenu :
        IsSearchOpen ? BigModeInputContext.Search :
        IsAlphabetNavigationOpen ? BigModeInputContext.Alphabet :
        IsAchievementsOverlayOpen ? BigModeInputContext.Achievements :
        IsHomeActive ? BigModeInputContext.Home : BigModeInputContext.Library;

    [ObservableProperty] private bool _isAlphabetNavigationOpen;
    private BigModeAlphabetIndex? _alphabetIndex;
    private IReadOnlyList<BigModeNavigationEntry>? _alphabetEntries;
    private bool _alphabetIndexDirty;
    private bool _alphabetRefreshPending;
    private bool _alphabetSelectionChanged;
    private string? _alphabetCulture;
    private bool _alphabetIgnoreArticles;
    private int _selectedAlphabetGroupIndex = -1;

    public string AlphabetTitle => T("BigMode_AlphabetTitle", "Alphabet selection");
    public string AlphabetControlsText => string.Format(
        T("BigMode_AlphabetControls", "D-pad / arrows · Navigate    {0} / {1} · Jump    {2} / {3} · Back"),
        ControllerButtonDisplayHelper.FormatCompact(_settings.ControllerBindings.Select),
        KeyboardBindingHelper.Format(_settings.KeyboardBindings.Select, Key.Enter),
        ControllerButtonDisplayHelper.FormatCompact(_settings.ControllerBindings.Back),
        KeyboardBindingHelper.Format(_settings.KeyboardBindings.Back, Key.Back));
    public IReadOnlyList<BigModeAlphabetGroup> AlphabetGroups => _alphabetIndex?.Groups ?? Array.Empty<BigModeAlphabetGroup>();
    public int AlphabetColumns => BigModeAlphabetIndex.Columns;
    public int SelectedAlphabetGroupIndex
    {
        get => _selectedAlphabetGroupIndex;
        set
        {
            if (value != -1 && (value < 0 || value >= AlphabetGroups.Count || !AlphabetGroups[value].IsAvailable)) return;
            SetProperty(ref _selectedAlphabetGroupIndex, value);
        }
    }

    private void OnGamepadAlphabet() => DispatchGamepadInput(() =>
    {
        if (IsAlphabetNavigationOpen) CloseAlphabetNavigation();
        else OpenAlphabetNavigation();
    });

    public bool OpenAlphabetNavigation()
    {
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress || IsHomeActive || IsAchievementsOverlayOpen) return false;
        if (IsSearchOpen) return false;
        if (IsAlphabetNavigationOpen) return true;
        FlushNavigationChanges();
        CloseSystemMenu();
        ResetAttractIdleTimer();
        EndNavigationScroll(resumePreview: true);
        CancelPreviewDebounce();
        _alphabetSelectionChanged = false;
        IsAlphabetNavigationOpen = true;
        RefreshAlphabetGroups(selectCurrent: true);
        return true;
    }

    public bool CloseAlphabetNavigation(bool resumePreview = true)
    {
        if (!IsAlphabetNavigationOpen) return false;
        IsAlphabetNavigationOpen = false;
        EndNavigationScroll(resumePreview: true);
        ResetAttractIdleTimer();
        if (resumePreview && _disposed == 0)
        {
            if (_alphabetSelectionChanged)
            {
                UpdateCircularItems();
                StopVideoIfPreviewPathChanged(ResolvePreviewVideoPath());
                ScheduleRetroAchievementsProgress(SelectedItem);
            }
            TriggerPreviewPlaybackWithDebounce();
        }
        _alphabetSelectionChanged = false;
        return true;
    }

    private void EnsureAlphabetIndex()
    {
        var entries = _navigation.Current.Entries;
        var culture = CultureInfo.CurrentCulture.Name;
        var ignoreArticles = MediaSortHelper.IgnoreLeadingArticlesInTitleSort;
        if (_alphabetIndex != null && !_alphabetIndexDirty && ReferenceEquals(entries, _alphabetEntries) &&
            culture == _alphabetCulture && ignoreArticles == _alphabetIgnoreArticles) return;
        _alphabetIndex = new BigModeAlphabetIndex(entries);
        _alphabetEntries = entries;
        _alphabetIndexDirty = false;
        _alphabetCulture = culture;
        _alphabetIgnoreArticles = ignoreArticles;
        OnPropertyChanged(nameof(AlphabetGroups));
    }

    private void RefreshAlphabetGroups(bool selectCurrent = false)
    {
        EnsureAlphabetIndex();
        var selected = selectCurrent && SelectedEntry != null ? BigModeAlphabetIndex.GetGroupIndex(SelectedEntry) : SelectedAlphabetGroupIndex;
        SelectedAlphabetGroupIndex = selected >= 0 && selected < AlphabetGroups.Count && AlphabetGroups[selected].IsAvailable
            ? selected : _alphabetIndex!.FirstAvailable;
    }

    private void InvalidateAlphabetIndex()
    {
        _alphabetIndexDirty = true;
        if (!IsAlphabetNavigationOpen || _alphabetRefreshPending) return;
        _alphabetRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _alphabetRefreshPending = false;
            if (_disposed == 0 && IsAlphabetNavigationOpen) RefreshAlphabetGroups();
        }, DispatcherPriority.Background);
    }

    private void OnAlphabetSortRuleChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed == 0) InvalidateAlphabetIndex();
    }, DispatcherPriority.Background);

    public void NavigateAlphabet(GamepadService.GamepadDirection direction, bool playSound = true)
    {
        if (!IsAlphabetNavigationOpen) return;
        FlushNavigationChanges();
        if (!IsAlphabetNavigationOpen) return;
        RefreshAlphabetGroups();
        ResetAttractIdleTimer();
        var dx = direction == GamepadService.GamepadDirection.Left ? -1 : direction == GamepadService.GamepadDirection.Right ? 1 : 0;
        var dy = direction == GamepadService.GamepadDirection.Up ? -1 : direction == GamepadService.GamepadDirection.Down ? 1 : 0;
        var target = _alphabetIndex!.Move(SelectedAlphabetGroupIndex, dx, dy);
        if (target == SelectedAlphabetGroupIndex) return;
        SelectedAlphabetGroupIndex = target;
        if (playSound) PlaySound(_theme.Sounds.Navigate);
    }

    public bool ConfirmAlphabetNavigation()
    {
        if (!IsAlphabetNavigationOpen || SelectedAlphabetGroupIndex < 0) return false;
        var groupIndex = SelectedAlphabetGroupIndex;
        FlushNavigationChanges();
        if (!IsAlphabetNavigationOpen) return false;
        RefreshAlphabetGroups();
        var group = AlphabetGroups[groupIndex];
        // Do not turn a vanished group into a jump to the newly highlighted group.
        if (!group.IsAvailable || group.TargetKey is not { } key)
        {
            _blockedKeyboardKeys.UnionWith(_pressedKeyboardKeys);
            return false;
        }
        // Consume overlay input before publishing the one target selection, so
        // context cleanup cannot cancel the incoming target's preload batch.
        CloseAlphabetNavigation(resumePreview: false);
        if (!_navigation.TrySelect(key, group.TargetIndex))
        {
            OpenAlphabetNavigation();
            return false;
        }
        TriggerPreviewPlaybackWithDebounce();
        PlaySound(_theme.Sounds.Confirm);
        return true;
    }
}
