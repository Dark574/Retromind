using Avalonia.Controls;

namespace Retromind.Views;

public partial class EditMediaView : Window
{
    public EditMediaView()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is System.IDisposable disposable)
            disposable.Dispose();
    }
}
