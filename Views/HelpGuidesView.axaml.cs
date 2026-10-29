using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class HelpGuidesView : Window
{
    public HelpGuidesView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
