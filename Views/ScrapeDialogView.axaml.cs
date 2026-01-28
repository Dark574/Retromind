using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Retromind.Views;

public partial class ScrapeDialogView : Window
{
    public ScrapeDialogView()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    private void CloseWindow(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
