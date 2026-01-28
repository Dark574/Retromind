using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class BulkScrapeView : Window
{
    private TextBox? _logBox;

    public BulkScrapeView()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
        
        // Find the log TextBox by its name.
        _logBox = this.FindControl<TextBox>("LogBox");
        
        if (_logBox != null)
        {
            // Listen to changes on the Text property.
            _logBox.PropertyChanged += OnLogBoxPropertyChanged;
        }
    }

    private void OnLogBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && sender is TextBox logBox)
            logBox.CaretIndex = int.MaxValue;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_logBox != null)
        {
            _logBox.PropertyChanged -= OnLogBoxPropertyChanged;
            _logBox = null;
        }

        if (DataContext is IDisposable disposable)
            disposable.Dispose();
    }
}
