using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class FolderImportProgressView : Window
{
    private bool _allowClose;

    public FolderImportProgressView()
    {
        InitializeComponent();
    }

    public void CompleteAndClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && DataContext is FolderImportProgressViewModel { IsRunning: true } viewModel)
        {
            e.Cancel = true;
            viewModel.RequestCancel();
            return;
        }

        base.OnClosing(e);
    }
}
