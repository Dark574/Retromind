using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Resources;

namespace Retromind.ViewModels;

/// <summary>
/// Simple ViewModel for a dialog prompting the user for a text input (e.g., naming a folder).
/// </summary>
public partial class NamePromptViewModel : ViewModelBase
{
    public delegate NamePromptValidationResult NamePromptValidator(string input);

    public sealed record NamePromptValidationResult(
        bool IsValid,
        string? ErrorMessage = null,
        string? SuggestedName = null);

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private string _suggestedName = string.Empty;

    [ObservableProperty]
    private bool _isInputValid = true;

    private readonly NamePromptValidator? _validator;

    public NamePromptViewModel(string title, string message, NamePromptValidator? validator = null)
    {
        Title = title;
        Message = message;
        _validator = validator;

        // Commands for dialog result handling
        // These commands typically close the window, returning true or false to the caller.
        OkCommand = new RelayCommand<Avalonia.Controls.Window>(win => win?.Close(true), _ => IsInputValid);
        CancelCommand = new RelayCommand<Avalonia.Controls.Window>(win => win?.Close(false));
        UseSuggestionCommand = new RelayCommand(ApplySuggestion, () => HasSuggestion);

        ValidateInput();
    }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand UseSuggestionCommand { get; }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(SuggestedName);

    public string SuggestionText =>
        string.IsNullOrWhiteSpace(SuggestedName)
            ? string.Empty
            : string.Format(Strings.Dialog_NamePrompt_SuggestionFormat, SuggestedName);

    partial void OnInputTextChanged(string value)
    {
        ValidateInput();
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    partial void OnSuggestedNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasSuggestion));
        OnPropertyChanged(nameof(SuggestionText));
        UseSuggestionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsInputValidChanged(bool value)
    {
        OkCommand.NotifyCanExecuteChanged();
    }

    private void ApplySuggestion()
    {
        if (!string.IsNullOrWhiteSpace(SuggestedName))
            InputText = SuggestedName;
    }

    private void ValidateInput()
    {
        if (_validator == null)
        {
            ValidationMessage = string.Empty;
            SuggestedName = string.Empty;
            IsInputValid = true;
            return;
        }

        var result = _validator(InputText);
        IsInputValid = result.IsValid;
        ValidationMessage = result.ErrorMessage ?? string.Empty;
        SuggestedName = result.SuggestedName ?? string.Empty;
    }
}
