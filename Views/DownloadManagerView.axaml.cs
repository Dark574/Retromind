using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Retromind.Views;

public partial class DownloadManagerView : Window
{
    public DownloadManagerView()
    {
        InitializeComponent();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
