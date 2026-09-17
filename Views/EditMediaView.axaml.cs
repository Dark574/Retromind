using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class EditMediaView : Window
{
    public EditMediaView()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is EditMediaViewModel { IsGogOperationRunning: true })
            e.Cancel = true;

        base.OnClosing(e);
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is System.IDisposable disposable)
            disposable.Dispose();
    }
}
