using System;
using Avalonia.Controls;

namespace Retromind.Views;

public partial class PasswordPromptView : Window
{
    public PasswordPromptView()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        this.FindControl<TextBox>("PasswordBox")?.Focus();
    }
}
