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

    // Diese Methode wird vom XAML "DoubleTapped" aufgerufen
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 1. ViewModel holen
        if (DataContext is SearchAreaViewModel vm && 
            // 2. Das angeklickte Item holen (steht im DataContext des Borders)
            sender is Control control && 
            control.DataContext is MediaItem item)
        {
            // 3. Im ViewModel setzen
            vm.SelectedMediaItem = item;
            
            // 4. Command ausführen
            if (vm.PlayCommand.CanExecute(item))
            {
                vm.PlayCommand.Execute(item);
            }
        }
    }
}