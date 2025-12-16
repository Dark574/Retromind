using Avalonia.Controls;
using Avalonia.Input;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class SearchAreaView : UserControl
{
    public SearchAreaView()
    {
        InitializeComponent();
    }

    // Triggered by XAML "DoubleTapped" on a result tile.
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        // The Border's DataContext is the bound item (MediaItem).
        if (sender is not Control { DataContext: MediaItem item })
            return;

        // Keep selection in sync (useful for the detail panel on the right).
        vm.SelectedMediaItem = item;

        // Execute play request via command (keeps the ViewModel as the single source of truth).
        if (vm.PlayCommand.CanExecute(item))
            vm.PlayCommand.Execute(item);
    }
}