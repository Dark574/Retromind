using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MetadataBackupView : Window
{
    private MetadataBackupViewModel? _viewModel;
    private bool _allowClose;

    public MetadataBackupView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as MetadataBackupViewModel;
        if (_viewModel == null)
            return;

        _viewModel.RequestConfirmation += ShowConfirmationAsync;
        _viewModel.RequestClose += CloseFromViewModel;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            await _viewModel.LoadAsync();
    }

    private async Task<bool> ShowConfirmationAsync(string message)
    {
        var confirm = new ConfirmView { DataContext = message };
        return await confirm.ShowDialog<bool>(this);
    }

    private void CloseFromViewModel(bool restoreCompleted)
    {
        _allowClose = true;
        Close(restoreCompleted);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && _viewModel?.IsBusy == true)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachViewModel();
    }

    private void DetachViewModel()
    {
        if (_viewModel == null)
            return;

        _viewModel.RequestConfirmation -= ShowConfirmationAsync;
        _viewModel.RequestClose -= CloseFromViewModel;
        _viewModel = null;
    }
}
