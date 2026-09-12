using System;
using Avalonia.Controls;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class BulkEditMediaView : Window
{
    private BulkEditMediaViewModel? _viewModel;

    public BulkEditMediaView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.RequestClose -= CloseFromViewModel;

        _viewModel = DataContext as BulkEditMediaViewModel;
        if (_viewModel != null)
            _viewModel.RequestClose += CloseFromViewModel;
    }

    private void CloseFromViewModel(bool accepted) => Close(accepted);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.RequestClose -= CloseFromViewModel;
            _viewModel = null;
        }
    }
}
