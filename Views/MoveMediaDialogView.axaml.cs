using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class MoveMediaDialogView : Window
{
    public MoveMediaDialogView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
