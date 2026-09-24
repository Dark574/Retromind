using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class InfoView : Window
{
    public static readonly StyledProperty<bool> ShowCopyButtonProperty =
        AvaloniaProperty.Register<InfoView, bool>(nameof(ShowCopyButton), true);

    public bool ShowCopyButton
    {
        get => GetValue(ShowCopyButtonProperty);
        set => SetValue(ShowCopyButtonProperty, value);
    }

    public InfoView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not string message)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(message);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
