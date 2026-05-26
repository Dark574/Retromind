using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class ProcessLogView : Window
{
    private TextBox? _logBox;

    public ProcessLogView()
    {
        InitializeComponent();
        Closed += OnWindowClosed;

        _logBox = this.FindControl<TextBox>("LogBox");
        if (_logBox != null)
            _logBox.PropertyChanged += OnLogBoxPropertyChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProcessLogViewModel vm)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null)
            return;

        await topLevel.Clipboard.SetTextAsync(vm.LogText ?? string.Empty);
    }

    private void OnLogBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty || sender is not TextBox logBox)
            return;

        var vm = DataContext as ProcessLogViewModel;
        logBox.CaretIndex = vm?.NewestFirst == true ? 0 : int.MaxValue;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_logBox != null)
        {
            _logBox.PropertyChanged -= OnLogBoxPropertyChanged;
            _logBox = null;
        }
    }
}
