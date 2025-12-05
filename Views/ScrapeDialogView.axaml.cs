using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Retromind.Views;

public partial class ScrapeDialogView : Window
{
    public ScrapeDialogView()
    {
        InitializeComponent();
    }

    private void CloseWindow(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}