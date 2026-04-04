using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class PasswordPromptViewModel : ViewModelBase
{
    public delegate string? PasswordValidator(string password, string confirmPassword);

    private readonly PasswordValidator? _validator;

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private bool _requireConfirmation;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    public PasswordPromptViewModel(
        string title,
        string message,
        bool requireConfirmation,
        PasswordValidator? validator = null)
    {
        Title = title;
        Message = message;
        RequireConfirmation = requireConfirmation;
        _validator = validator;

        OkCommand = new RelayCommand<Avalonia.Controls.Window>(OnOk);
        CancelCommand = new RelayCommand<Avalonia.Controls.Window>(win => win?.Close(false));
    }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string PasswordLabel => T("Common.Password", "Password");
    public string ConfirmPasswordLabel => T("Parental.Prompt.ConfirmPassword", "Confirm password");
    public string OkButtonText => T("Button.Ok", "OK");
    public string CancelButtonText => T("Button.Cancel", "Cancel");

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    private void OnOk(Avalonia.Controls.Window? win)
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Password))
        {
            ValidationMessage = T("Parental.Validation.PasswordRequired", "Password is required.");
            return;
        }

        if (RequireConfirmation && !string.Equals(Password, ConfirmPassword, System.StringComparison.Ordinal))
        {
            ValidationMessage = T("Parental.Validation.PasswordsDoNotMatch", "Passwords do not match.");
            return;
        }

        var validatorError = _validator?.Invoke(Password, ConfirmPassword);
        if (!string.IsNullOrWhiteSpace(validatorError))
        {
            ValidationMessage = validatorError;
            return;
        }

        win?.Close(true);
    }
}
