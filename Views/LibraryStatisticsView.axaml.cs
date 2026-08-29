using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class LibraryStatisticsView : Window
{
    private LibraryStatisticsViewModel? _viewModel;

    public LibraryStatisticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.RequestClose -= CloseFromViewModel;

        _viewModel = DataContext as LibraryStatisticsViewModel;
        if (_viewModel != null)
            _viewModel.RequestClose += CloseFromViewModel;
    }

    private void CloseFromViewModel()
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.RequestClose -= CloseFromViewModel;
            _viewModel = null;
        }
    }
}
