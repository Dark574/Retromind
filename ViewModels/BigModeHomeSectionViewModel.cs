using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

public sealed partial class BigModeHomeSectionViewModel : ObservableObject
{
    private readonly IReadOnlyList<BigModeHomeEntryViewModel>? _pagedEntries;
    private readonly int _pageSize;

    public BigModeHomeSectionViewModel(
        string title,
        ObservableCollection<BigModeHomeEntryViewModel> entries,
        BigModeHomeLibraryNavigator? libraryNavigator = null)
    {
        Title = title;
        Entries = entries;
        LibraryNavigator = libraryNavigator;
        Breadcrumb = libraryNavigator?.Breadcrumb;
        PreviousPageCommand = new RelayCommand(() => MovePage(-1, selectLastEntry: true));
        NextPageCommand = new RelayCommand(() => MovePage(1, selectLastEntry: false));
    }

    public BigModeHomeSectionViewModel(
        string title,
        IReadOnlyList<BigModeHomeEntryViewModel> entries,
        int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        Title = title;
        Entries = [];
        _pagedEntries = entries;
        _pageSize = pageSize;
        PreviousPageCommand = new RelayCommand(() => MovePage(-1, selectLastEntry: true));
        NextPageCommand = new RelayCommand(() => MovePage(1, selectLastEntry: false));
        RefreshPage();
    }

    public string Title { get; }
    public ObservableCollection<BigModeHomeEntryViewModel> Entries { get; }
    public BigModeHomeLibraryNavigator? LibraryNavigator { get; }
    public bool IsLibraryNavigation => LibraryNavigator != null;
    public bool IsPaged => PageCount > 1;
    public int PageCount => _pagedEntries == null
        ? 1
        : Math.Max(1, (_pagedEntries.Count + _pageSize - 1) / _pageSize);
    public string? PageIndicator => IsPaged ? $"{CurrentPageIndex + 1}/{PageCount}" : null;
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }

    [ObservableProperty]
    private int _currentPageIndex;

    [ObservableProperty]
    private string? _breadcrumb;

    [ObservableProperty]
    private BigModeHomeEntryViewModel? _selectedEntry;

    public event Action<BigModeHomeSectionViewModel, BigModeHomeEntryViewModel?>? SelectionChanged;

    public bool MovePage(int delta, bool selectLastEntry)
    {
        if (!IsPaged || delta == 0)
            return false;

        CurrentPageIndex = (CurrentPageIndex + delta + PageCount) % PageCount;
        RefreshPage();
        SelectedEntry = selectLastEntry ? Entries[^1] : Entries[0];
        return true;
    }

    public BigModeHomeEntryViewModel? FindEntry(
        Func<BigModeHomeEntryViewModel, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var source = _pagedEntries ?? Entries;
        foreach (var entry in source)
        {
            if (predicate(entry))
                return entry;
        }

        return null;
    }

    public void RevealEntry(BigModeHomeEntryViewModel entry)
    {
        if (_pagedEntries == null)
            return;

        for (var index = 0; index < _pagedEntries.Count; index++)
        {
            if (!ReferenceEquals(_pagedEntries[index], entry))
                continue;

            var pageIndex = index / _pageSize;
            if (pageIndex != CurrentPageIndex)
            {
                CurrentPageIndex = pageIndex;
                RefreshPage();
            }

            return;
        }
    }

    public void RefreshLibraryEntries()
    {
        if (LibraryNavigator == null)
            return;

        Entries.Clear();
        foreach (var entry in LibraryNavigator.BuildEntries())
            Entries.Add(entry);
        Breadcrumb = LibraryNavigator.Breadcrumb;
    }

    private void RefreshPage()
    {
        if (_pagedEntries == null)
            return;

        Entries.Clear();

        var startIndex = CurrentPageIndex * _pageSize;
        var endIndex = Math.Min(startIndex + _pageSize, _pagedEntries.Count);
        for (var index = startIndex; index < endIndex; index++)
            Entries.Add(_pagedEntries[index]);

        OnPropertyChanged(nameof(PageIndicator));
    }

    partial void OnSelectedEntryChanged(BigModeHomeEntryViewModel? value) =>
        SelectionChanged?.Invoke(this, value);
}
