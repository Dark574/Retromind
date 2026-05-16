using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class GogPickerDialogView : Window
{
    public GogPickerDialogView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
