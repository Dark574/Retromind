using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel acting as the DataContext for the Big Picture / Themed mode.
/// Theme creators will bind to properties exposed here.
/// </summary>
public partial class BigModeViewModel : ViewModelBase
{
    // Collection of media items to display in the theme
    [ObservableProperty]
    private ObservableCollection<MediaItem> _items;

    // The currently selected item (for navigation and details)
    [ObservableProperty]
    private MediaItem? _selectedItem;
    
    // Title of the current context (e.g., "SNES" or "Favorites")
    [ObservableProperty]
    private string _categoryTitle = "Library";

    /// <summary>
    /// Event triggered when the Big Mode should be closed (returning to desktop mode).
    /// </summary>
    public event Action? RequestClose;

    public BigModeViewModel(ObservableCollection<MediaItem> items, string title)
    {
        Items = items;
        CategoryTitle = title;
        
        // Select the first item by default if available
        if (Items.Count > 0) 
            SelectedItem = Items[0];
    }

    /// <summary>
    /// Command to exit the Big Mode.
    /// </summary>
    [RelayCommand]
    private void ExitBigMode()
    {
        RequestClose?.Invoke();
    }
    
    /// <summary>
    /// Command to launch the currently selected media.
    /// </summary>
    [RelayCommand]
    private void PlayCurrent()
    {
        if (SelectedItem == null) return;

        // Logic to launch the game/movie will go here.
        // For now, we just log or placeholders.
        System.Diagnostics.Debug.WriteLine($"Launching: {SelectedItem.Title}");
    }
    
    // Navigation helper commands (useful for controller mapping later)
    [RelayCommand]
    private void SelectNext()
    {
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index < Items.Count - 1)
            SelectedItem = Items[index + 1];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index > 0)
            SelectedItem = Items[index - 1];
    }
}