using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Retromind.Views;

public partial class NamePromptView : Window
{
    public NamePromptView()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Set focus to the input field (NameBox must exist in the XAML).
        this.FindControl<TextBox>("NameBox")?.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}