using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MediaAreaView : UserControl
{
    public MediaAreaView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // NEU: Diese Methode leitet den Doppelklick an das ViewModel weiter
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MediaAreaViewModel vm)
            if (vm.DoubleClickCommand.CanExecute(null))
                vm.DoubleClickCommand.Execute(null);
    }
}