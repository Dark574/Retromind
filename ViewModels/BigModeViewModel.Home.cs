using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private sealed record LibraryViewState(
        bool IsGameListActive,
        MediaItem? SelectedItem,
        int SelectedItemIndex,
        MediaNode? ThemeContextNode);

    private LibraryViewState? _libraryViewStateBeforeHome;
    private bool _updatingHomeSelection;
    private int _homeTransitionGeneration;

    public bool SupportsHome { get; private set; }
    public ObservableCollection<BigModeHomeSectionViewModel> HomeSections { get; private set; } = [];

    public string HomeTitle => "RETROMIND";
    public string HomeLabel => T("BigMode.Home.Title", "Home");
    public string HomeHintText => T(
        "BigMode.Home.Hint",
        "Y / Triangle · Library    A / Cross · Open");

    [ObservableProperty]
    private bool _isHomeActive;

    [ObservableProperty]
    private BigModeHomeSectionViewModel? _selectedHomeSection;

    [ObservableProperty]
    private BigModeHomeEntryViewModel? _selectedHomeEntry;

    partial void OnSelectedHomeSectionChanged(BigModeHomeSectionViewModel? value)
    {
        if (_updatingHomeSelection || !IsHomeActive || value == null)
            return;

        SelectHomeSection(value);
    }

    private void InitializeHome(bool startOnHome)
    {
        SupportsHome = _settings.EnableBigModeHomeScreen && _theme.SupportsHome;
        OnPropertyChanged(nameof(SupportsHome));

        if (!SupportsHome)
            return;

        RefreshHomeSections(preserveSelection: false);

        if (startOnHome)
            EnterHomeImmediately();
    }

    private void RefreshHomeSections(bool preserveSelection)
    {
        var previousSectionTitle = preserveSelection ? SelectedHomeSection?.Title : null;
        var previousItem = preserveSelection ? SelectedHomeEntry?.Item : null;
        var previousNode = preserveSelection ? SelectedHomeEntry?.Node : null;
        var previousKind = preserveSelection ? SelectedHomeEntry?.Kind : null;
        var previousLibraryNode = preserveSelection
            ? HomeSections.Select(section => section.LibraryNavigator)
                .FirstOrDefault(navigator => navigator != null)?.CurrentNode
            : null;

        foreach (var section in HomeSections)
            section.SelectionChanged -= OnHomeSectionSelectionChanged;

        HomeSections = BigModeHomeBuilder.Build(
            _rootNodes,
            _parentalFilterActive,
            T("Statistics.RecentlyPlayed", "Recently played"),
            T("Statistics.Favorites", "Favorites"),
            Strings.Media_Library,
            ThemeStrings.GameCountLabel,
            T("BigMode.Home.Library.Back", "Back"),
            T("BigMode.Home.Library.Open", "Open"),
            T("BigMode.Home.Library.Overview", "Overview"),
            previousLibraryNode);
        OnPropertyChanged(nameof(HomeSections));

        foreach (var section in HomeSections)
            section.SelectionChanged += OnHomeSectionSelectionChanged;

        var selectedSection = HomeSections.FirstOrDefault(section =>
                                  string.Equals(section.Title, previousSectionTitle, StringComparison.CurrentCulture))
                              ?? HomeSections.FirstOrDefault();
        var selectedEntry = selectedSection?.Entries.FirstOrDefault(entry =>
                                entry.Kind == previousKind &&
                                ((previousItem != null && ReferenceEquals(entry.Item, previousItem)) ||
                                 (previousNode != null && ReferenceEquals(entry.Node, previousNode))))
                            ?? selectedSection?.Entries.FirstOrDefault();

        SelectedHomeSection = selectedSection;
        SelectedHomeEntry = selectedEntry;
        if (selectedSection != null)
            selectedSection.SelectedEntry = selectedEntry;
    }

    private void EnterHomeImmediately()
    {
        CaptureLibraryViewState();
        IsHomeActive = true;
        ThemeContextNode = null;
        ApplyHomeEntry(SelectedHomeEntry);
    }

    [RelayCommand]
    private async Task ToggleHomeAsync()
    {
        if (!SupportsHome || _isLaunching)
            return;

        ResetAttractIdleTimer();
        CloseAchievementsOverlay();
        StopGamepadRepeatTimer();

        var generation = Interlocked.Increment(ref _homeTransitionGeneration);
        await PrepareForHomeTransitionAsync();

        if (generation != Volatile.Read(ref _homeTransitionGeneration))
            return;

        if (IsHomeActive)
            RestoreLibraryViewState();
        else
        {
            CaptureLibraryViewState();
            RefreshHomeSections(preserveSelection: true);
            IsHomeActive = true;
            ThemeContextNode = null;
            ApplyHomeEntry(SelectedHomeEntry);
        }

        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        if (generation != Volatile.Read(ref _homeTransitionGeneration))
            return;

        TriggerPreviewPlaybackWithDebounce();
        EnsureSecondaryBackgroundPlayingIfReady();
    }

    private async Task PrepareForHomeTransitionAsync()
    {
        CancelPreviewDebounce();
        StopVideo();

        try
        {
            if (_secondaryPlayer is { IsPlaying: true })
                _secondaryPlayer.Stop();
        }
        catch
        {
            // Preview transitions are best effort and must not block navigation.
        }

        SecondaryVideoIsPlaying = false;
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private void CaptureLibraryViewState()
    {
        if (_libraryViewStateBeforeHome != null)
            return;

        _libraryViewStateBeforeHome = new LibraryViewState(
            IsGameListActive,
            SelectedItem,
            SelectedItemIndex,
            ThemeContextNode);
    }

    private void RestoreLibraryViewState()
    {
        var state = _libraryViewStateBeforeHome;
        if (state == null)
        {
            IsHomeActive = false;
            return;
        }

        // Restore the hidden library state while Home is still active. This
        // prevents ThemeContextNode from needlessly recalculating fallbacks for
        // every item in a large node; those overrides never changed on Home.
        ThemeContextNode = state.ThemeContextNode;
        IsGameListActive = state.IsGameListActive;
        SelectedItem = state.SelectedItem;
        SelectedItemIndex = state.SelectedItemIndex;
        _libraryViewStateBeforeHome = null;
        IsHomeActive = false;
        NotifyHomeSelectionPropertiesChanged();
    }

    private void OnHomeSectionSelectionChanged(
        BigModeHomeSectionViewModel section,
        BigModeHomeEntryViewModel? entry)
    {
        if (_updatingHomeSelection || entry == null)
            return;

        _updatingHomeSelection = true;
        try
        {
            foreach (var other in HomeSections)
            {
                if (!ReferenceEquals(other, section) && other.SelectedEntry != null)
                    other.SelectedEntry = null;
            }

            SelectedHomeSection = section;
            SelectedHomeEntry = entry;
            if (IsHomeActive)
                ApplyHomeEntry(entry);
        }
        finally
        {
            _updatingHomeSelection = false;
        }
    }

    private void ApplyHomeEntry(BigModeHomeEntryViewModel? entry)
    {
        SelectedHomeEntry = entry;

        if (entry?.Item != null)
        {
            IsGameListActive = true;
            SelectedItem = entry.Item;
            SelectedItemIndex = -1;
        }
        else
        {
            IsGameListActive = false;
            SelectedItem = null;
            SelectedItemIndex = -1;
        }

        NotifyHomeSelectionPropertiesChanged();
    }

    private void NotifyHomeSelectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(ActiveCategoryLogoPath));
        OnPropertyChanged(nameof(ActiveCategoryWallpaperPath));
        OnPropertyChanged(nameof(ActiveLogoPath));
        OnPropertyChanged(nameof(HasDisplayLogo));
        OnPropertyChanged(nameof(ActiveWallpaperPath));
        OnPropertyChanged(nameof(ActiveScreenshotFallbackPath));
        OnPropertyChanged(nameof(DynamicAccentArtworkPath));
        OnPropertyChanged(nameof(ActiveVideoPath));
        OnPropertyChanged(nameof(ActiveMarqueePath));
        RequestActiveBezelRefresh();
        OnPropertyChanged(nameof(ActiveControlPanelPath));
        TriggerPreviewPlaybackWithDebounce();
    }

    public void NavigateFromKeyboard(GamepadService.GamepadDirection direction)
    {
        if (_isLaunching)
            return;

        if (IsAchievementsOverlayOpen)
        {
            NavigateAchievementsOverlay(direction);
            return;
        }

        if (IsHomeActive)
        {
            PlaySound(_theme.Sounds.Navigate);
            NavigateHome(direction);
            return;
        }

        switch (direction)
        {
            case GamepadService.GamepadDirection.Up:
            case GamepadService.GamepadDirection.Left:
                SelectPreviousCommand.Execute(null);
                break;
            case GamepadService.GamepadDirection.Down:
            case GamepadService.GamepadDirection.Right:
                SelectNextCommand.Execute(null);
                break;
        }
    }

    private void NavigateHome(GamepadService.GamepadDirection direction)
    {
        if (HomeSections.Count == 0)
            return;

        var sectionIndex = SelectedHomeSection == null
            ? 0
            : HomeSections.IndexOf(SelectedHomeSection);
        if (sectionIndex < 0)
            sectionIndex = 0;

        if (direction is GamepadService.GamepadDirection.Up or GamepadService.GamepadDirection.Down)
        {
            var delta = direction == GamepadService.GamepadDirection.Up ? -1 : 1;
            sectionIndex = (sectionIndex + delta + HomeSections.Count) % HomeSections.Count;
            SelectHomeSection(HomeSections[sectionIndex]);
            return;
        }

        var section = HomeSections[sectionIndex];
        if (section.Entries.Count == 0)
            return;

        var entryIndex = section.SelectedEntry == null ? 0 : section.Entries.IndexOf(section.SelectedEntry);
        if (entryIndex < 0)
            entryIndex = 0;

        var entryDelta = direction == GamepadService.GamepadDirection.Left ? -1 : 1;
        entryIndex = (entryIndex + entryDelta + section.Entries.Count) % section.Entries.Count;
        section.SelectedEntry = section.Entries[entryIndex];
    }

    private void SelectHomeSection(BigModeHomeSectionViewModel section)
    {
        var entry = section.SelectedEntry ?? section.Entries.FirstOrDefault();
        if (entry == null)
            return;

        if (!ReferenceEquals(section.SelectedEntry, entry))
            section.SelectedEntry = entry;
        else
            OnHomeSectionSelectionChanged(section, entry);
    }

    private bool NavigateHomeLibrary(BigModeHomeEntryViewModel entry)
    {
        var section = SelectedHomeSection;
        var navigator = section?.LibraryNavigator;
        if (section == null || navigator == null)
            return false;

        var changed = entry.Kind switch
        {
            BigModeHomeEntryKind.LibraryBack => navigator.GoBack(),
            BigModeHomeEntryKind.LibraryChild when entry.Node != null && entry.HasChildren =>
                navigator.DrillInto(entry.Node),
            _ => false
        };
        if (!changed)
            return false;

        _updatingHomeSelection = true;
        try
        {
            section.SelectedEntry = null;
            section.RefreshLibraryEntries();
            var current = section.Entries.FirstOrDefault(candidate => candidate.IsLibraryCurrent)
                          ?? section.Entries.FirstOrDefault();
            section.SelectedEntry = current;
            SelectedHomeSection = section;
            SelectedHomeEntry = current;
        }
        finally
        {
            _updatingHomeSelection = false;
        }

        ApplyHomeEntry(SelectedHomeEntry);
        PlaySound(_theme.Sounds.Navigate);
        return true;
    }

    private void DisposeHome()
    {
        foreach (var section in HomeSections)
            section.SelectionChanged -= OnHomeSectionSelectionChanged;
    }

    private MediaNode? GetActiveItemSourceNode() =>
        IsHomeActive ? SelectedHomeEntry?.SourceNode : ThemeContextNode ?? CurrentNode;

    private async Task OpenHomeLibraryNodeAsync(MediaNode node)
    {
        ResetToRootState();

        if (!TryBuildNavigationPathFromNodeId(node.Id, out var path))
            return;

        foreach (var nodeId in path)
        {
            var next = CurrentCategories.FirstOrDefault(candidate => candidate.Id == nodeId);
            if (next == null)
                return;

            SelectedCategory = next;
            await PlayCurrent();
        }
    }
}
