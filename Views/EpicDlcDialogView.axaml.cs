using System;
using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class EpicDlcDialogView : Window
{
    private EpicDlcDialogViewModel? _viewModel;

    public EpicDlcDialogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.RequestClose -= CloseFromViewModel;

        _viewModel = DataContext as EpicDlcDialogViewModel;
        if (_viewModel != null)
            _viewModel.RequestClose += CloseFromViewModel;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            await _viewModel.LoadAsync();
    }

    private void CloseFromViewModel() => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel == null)
            return;

        _viewModel.RequestClose -= CloseFromViewModel;
        _viewModel.Dispose();
        _viewModel = null;
    }
}
