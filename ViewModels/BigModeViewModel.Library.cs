using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Threading;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.BigMode;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private BigModeNavigationSession _navigation = null!;
    private BigModeLibraryObserver? _libraryObserver;
    private bool _synchronizingNavigation;
    private IReadOnlyList<BigModeNavigationEntry>? _projectedEntries;
    private readonly RangeObservableCollection<BigModeNavigationEntry> _wheelEntries = new();
    private bool _usesWheelEntries;
    private int _wheelSelectedEntryIndex = -1;
    private readonly List<int> _mediaEntryPositions = [];

    public IReadOnlyList<BigModeNavigationEntry> NavigationEntries => _navigation.Current.Entries;
    public BigModeNavigationEntry? SelectedEntry
    {
        get => _navigation.Current.SelectedEntry;
        set { if (!_synchronizingNavigation && value != null) _navigation.TrySelect(value.Key, SelectedEntryIndex); }
    }
    public int SelectedEntryIndex
    {
        get => _navigation.Current.SelectedIndex;
        set { if (!_synchronizingNavigation) _navigation.TrySelect(value); }
    }
    public bool HasChildNodes => CurrentCategories.Count > 0;
    public string? SystemMediaArtworkPath => SelectedItem?.PrimaryScreenshotPath ?? ActiveWallpaperPath ?? SelectedEntry?.CoverPath;
    public string NavigationTitle => Strings.Media_Library;
    public bool IsNavigationEmpty => !IsHomeActive && NavigationEntries.Count == 0;
    public string HostNavigationActionText => T("BigMode.Home.Library.Open", "Open");
    public string EmptyLibraryText => T("BigMode.Empty", "No visible entries");
    public NavigationPresentation NavigationPresentation => _theme.NavigationPresentation;
    public bool ShowHostNavigation => !IsHomeActive &&
        (NavigationPresentation is NavigationPresentation.MediaItemsOnly or NavigationPresentation.LegacySeparate) &&
        (!IsGameListActive || HasChildNodes);

    public ObservableCollection<BigModeNavigationEntry> WheelEntries
    {
        get { EnsureNavigationWheel(); return _wheelEntries; }
    }
    public int WheelSelectedEntryIndex
    {
        get { EnsureNavigationWheel(); return _wheelSelectedEntryIndex; }
    }

    private void InitializeNavigation()
    {
        _navigation = new BigModeNavigationSession(_rootNodes, _parentalFilterActive);
        BigModeNavigationPersistence.Restore(_navigation, _settings);
        _navigation.Changed += OnNavigationChanged;
        SynchronizeNavigation();
        _libraryObserver = new BigModeLibraryObserver(_rootNodes, () =>
            Dispatcher.UIThread.Post(FlushNavigationChanges, DispatcherPriority.Background), OnLibraryPresentationChanged);
    }

    public void SetParentalFilterActive(bool active)
    {
        if (_disposed != 0 || active == _parentalFilterActive) return;
        _parentalFilterActive = active;
        ResetAttractIdleTimer();
        _navigation.Refresh(active);
        _libraryObserver?.Rebind();
        if (SupportsHome)
        {
            RefreshHomeSections(preserveSelection: true);
            if (IsHomeActive) ApplyHomeEntry(SelectedHomeEntry);
        }
    }

    private bool _presentationRefreshPending;
    private bool _refreshingPresentation;
    private bool _pendingFallbackRefresh;

    private void OnLibraryPresentationChanged(INotifyPropertyChanged model, string? property)
    {
        if (_refreshingPresentation || _synchronizingNavigation || _disposed != 0) return;
        var source = GetActiveItemSourceNode();
        if (!ReferenceEquals(model, SelectedItem) && !ReferenceEquals(model, SelectedCategory) &&
            !ReferenceEquals(model, source)) return;
        _pendingFallbackRefresh |= ReferenceEquals(model, source) &&
            (property == "Assets" || property?.EndsWith("FallbackEnabled", StringComparison.Ordinal) == true ||
             property?.StartsWith("Primary", StringComparison.Ordinal) == true);
        if (_presentationRefreshPending) return;
        _presentationRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _presentationRefreshPending = false;
            if (_disposed != 0) return;
            _refreshingPresentation = true;
            try
            {
                if (_pendingFallbackRefresh && !IsHomeActive) ApplyNodeFallbackOverrides();
                _pendingFallbackRefresh = false;
                _itemVideoPathCache.Clear();
                _nodeVideoPathCache.Clear();
                CategoryTitle = CurrentNode?.Name ?? Strings.BigMode_MainMenu;
                StopVideoIfPreviewPathChanged(ResolvePreviewVideoPath());
                NotifyHomeSelectionPropertiesChanged();
            }
            finally { _refreshingPresentation = false; }
        }, DispatcherPriority.Background);
    }

    private void OnNavigationChanged(object? sender, EventArgs args)
    {
        if (!_applyingAttractSelection) ResetAttractIdleTimer();
        SynchronizeNavigation();
    }

    private void FlushNavigationChanges()
    {
        if (_disposed != 0 || _libraryObserver?.IsDirty != true) return;
        ResetAttractIdleTimer();
        _itemVideoPathCache.Clear();
        _nodeVideoPathCache.Clear();
        _navigation.Refresh();
        _libraryObserver.Rebind();
        if (SupportsHome)
        {
            RefreshHomeSections(preserveSelection: true);
            if (IsHomeActive) ApplyHomeEntry(SelectedHomeEntry);
        }
    }

    private void SynchronizeNavigation()
    {
        var frame = _navigation.Current;
        var newLevel = !ReferenceEquals(_projectedEntries, frame.Entries);
        var ownerChanged = !ReferenceEquals(CurrentNode, frame.OpenedNode);
        _synchronizingNavigation = true;
        try
        {
            // Resolve/gate a root-theme change before collections, text or media
            // bindings in the outgoing theme can see the incoming selection.
            CurrentNode = frame.OpenedNode;
            if (!IsHomeActive) ThemeContextNode = frame.OpenedNode;
            if (newLevel)
            {
                ClearNodeFallbackOverrides(Items);
                _projectedEntries = frame.Entries;
                CurrentCategories = new(frame.Entries.OfType<BigModeNodeNavigationEntry>().Select(entry => entry.Node));
                Items = new(frame.Entries.OfType<BigModeMediaNavigationEntry>().Select(entry => entry.Item));
                _mediaEntryPositions.Clear();
                for (var i = CurrentCategories.Count; i < frame.Entries.Count; i++) _mediaEntryPositions.Add(i);
                if (frame.OpenedNode != null) ApplyRandomizedMedia(frame.OpenedNode, Items);
                _itemVideoPathCache.Clear();
            }
            CategoryTitle = frame.OpenedNode?.Name ?? Strings.BigMode_MainMenu;
            if (!IsHomeActive)
            {
                SelectedCategoryIndex = frame.SelectedEntry is BigModeNodeNavigationEntry ? frame.SelectedIndex : -1;
                SelectedItemIndex = frame.SelectedEntry is BigModeMediaNavigationEntry ? frame.SelectedIndex - CurrentCategories.Count : -1;
                SelectedCategory = (frame.SelectedEntry as BigModeNodeNavigationEntry)?.Node;
                SelectedItem = (frame.SelectedEntry as BigModeMediaNavigationEntry)?.Item;
                IsGameListActive = SelectedItem != null;
            }
            if (newLevel || ownerChanged) ApplyNodeFallbackOverrides();
            if (newLevel) { OnPropertyChanged(nameof(NavigationEntries)); OnPropertyChanged(nameof(HasChildNodes)); }
            UpdateGameCounters();
            UpdateCircularItems();
            UpdateWheelItems();
            UpdateWheelCategories();
            UpdateNavigationWheel();
            if (!IsHomeActive)
            {
                StopVideoIfPreviewPathChanged(ResolvePreviewVideoPath());
                ScheduleRetroAchievementsProgress(SelectedItem);
                NotifyHomeSelectionPropertiesChanged();
            }
            OnPropertyChanged(nameof(SelectedEntryIndex));
            OnPropertyChanged(nameof(SelectedEntry));
            OnPropertyChanged(nameof(ShowHostNavigation));
            OnPropertyChanged(nameof(IsNavigationEmpty));
        }
        finally { _synchronizingNavigation = false; }
    }

    private void EnsureNavigationWheel()
    {
        if (_usesWheelEntries) return;
        _usesWheelEntries = true;
        UpdateNavigationWheel();
    }

    private void UpdateNavigationWheel()
    {
        if (!_usesWheelEntries || _navigation == null) return;
        _wheelSelectedEntryIndex = CircularWindowHelper.SynchronizeRepeatingCircularWindow(
            (IList<BigModeNavigationEntry>)NavigationEntries, SelectedEntry, _circularWindowSize,
            MinimumRepeatingWheelItemCount, _wheelEntries, SelectedEntryIndex);
        OnPropertyChanged(nameof(WheelSelectedEntryIndex));
    }

    public void SaveState()
    {
        if (_stateSaved) return;
        _stateSaved = true;
        FlushNavigationChanges();
        BigModeNavigationPersistence.Save(_navigation, _settings);
    }

    private void DisposeNavigation()
    {
        _libraryObserver?.Dispose();
        if (_navigation != null) _navigation.Changed -= OnNavigationChanged;
    }
}
