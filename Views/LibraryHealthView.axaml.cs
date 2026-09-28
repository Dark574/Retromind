using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class LibraryHealthView : Window
{
    private LibraryHealthViewModel? _viewModel;

    public LibraryHealthView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as LibraryHealthViewModel;
        if (_viewModel == null)
            return;

        _viewModel.RequestClose += CloseFromViewModel;
        _viewModel.RequestCopy += CopyToClipboardAsync;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            await _viewModel.LoadAsync();
    }

    private void CloseFromViewModel() => Close();

    private async Task CopyToClipboardAsync(string text)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(text);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DetachViewModel();
    }

    private void DetachViewModel()
    {
        if (_viewModel == null)
            return;

        _viewModel.RequestClose -= CloseFromViewModel;
        _viewModel.RequestCopy -= CopyToClipboardAsync;
        _viewModel.Dispose();
        _viewModel = null;
    }
}
