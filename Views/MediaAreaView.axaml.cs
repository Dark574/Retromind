using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MediaAreaView : UserControl
{
    public MediaAreaView()
    {
        InitializeComponent();
        
        // Ensure we run our scroll logic once the control is loaded.
        this.Loaded += OnLoadedOnce;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // transfers the double click to the view model
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MediaAreaViewModel vm)
            if (vm.DoubleClickCommand.CanExecute(null))
                vm.DoubleClickCommand.Execute(null);
    }
    
    /// <summary>
    /// Called once when the control is loaded. Scrolls the current SelectedMediaItem
    /// into view so that the last-used item is visible after restoring a node
    /// </summary>
    private void OnLoadedOnce(object? sender, RoutedEventArgs e)
    {
        // We only need this once per view instance.
        this.Loaded -= OnLoadedOnce;

        // Defer to the UI thread after layout so that containers are created.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MediaAreaViewModel vm)
                return;

            if (vm.SelectedMediaItem is null)
                return;

            if (MediaList is null)
                return;

            // Let Avalonia generate containers and scroll the item into view.
            MediaList.ScrollIntoView(vm.SelectedMediaItem);
        }, DispatcherPriority.Background);
    }
}