using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.ViewModels;

public sealed partial class BigModeHomeSectionViewModel : ObservableObject
{
    public BigModeHomeSectionViewModel(
        string title,
        ObservableCollection<BigModeHomeEntryViewModel> entries,
        BigModeHomeLibraryNavigator? libraryNavigator = null)
    {
        Title = title;
        Entries = entries;
        LibraryNavigator = libraryNavigator;
        Breadcrumb = libraryNavigator?.Breadcrumb;
    }

    public string Title { get; }
    public ObservableCollection<BigModeHomeEntryViewModel> Entries { get; }
    public BigModeHomeLibraryNavigator? LibraryNavigator { get; }
    public bool IsLibraryNavigation => LibraryNavigator != null;

    [ObservableProperty]
    private string? _breadcrumb;

    [ObservableProperty]
    private BigModeHomeEntryViewModel? _selectedEntry;

    public event Action<BigModeHomeSectionViewModel, BigModeHomeEntryViewModel?>? SelectionChanged;

    public void RefreshLibraryEntries()
    {
        if (LibraryNavigator == null)
            return;

        Entries.Clear();
        foreach (var entry in LibraryNavigator.BuildEntries())
            Entries.Add(entry);
        Breadcrumb = LibraryNavigator.Breadcrumb;
    }

    partial void OnSelectedEntryChanged(BigModeHomeEntryViewModel? value) =>
        SelectionChanged?.Invoke(this, value);
}
