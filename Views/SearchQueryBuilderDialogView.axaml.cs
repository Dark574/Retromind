using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class SearchQueryBuilderDialogView : Window
{
    public SearchQueryBuilderDialogView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
