using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Retromind.Views;

public partial class MetadataAutoCompleteTextBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MetadataAutoCompleteTextBox, string?>(nameof(Text), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> SuggestionSuffixProperty =
        AvaloniaProperty.Register<MetadataAutoCompleteTextBox, string?>(nameof(SuggestionSuffix));

    public static readonly StyledProperty<ICommand?> AcceptSuggestionCommandProperty =
        AvaloniaProperty.Register<MetadataAutoCompleteTextBox, ICommand?>(nameof(AcceptSuggestionCommand));

    public static readonly StyledProperty<object?> AcceptSuggestionCommandParameterProperty =
        AvaloniaProperty.Register<MetadataAutoCompleteTextBox, object?>(nameof(AcceptSuggestionCommandParameter));

    public MetadataAutoCompleteTextBox()
    {
        InitializeComponent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? SuggestionSuffix
    {
        get => GetValue(SuggestionSuffixProperty);
        set => SetValue(SuggestionSuffixProperty, value);
    }

    public ICommand? AcceptSuggestionCommand
    {
        get => GetValue(AcceptSuggestionCommandProperty);
        set => SetValue(AcceptSuggestionCommandProperty, value);
    }

    public object? AcceptSuggestionCommandParameter
    {
        get => GetValue(AcceptSuggestionCommandParameterProperty);
        set => SetValue(AcceptSuggestionCommandParameterProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Right || sender is not TextBox textBox)
            return;

        var text = textBox.Text ?? string.Empty;
        if (!string.IsNullOrEmpty(textBox.SelectedText) || textBox.CaretIndex != text.Length)
            return;

        var command = AcceptSuggestionCommand;
        var parameter = AcceptSuggestionCommandParameter;
        if (command == null || !command.CanExecute(parameter))
            return;

        command.Execute(parameter);
        textBox.CaretIndex = textBox.Text?.Length ?? 0;
        e.Handled = true;
    }
}
