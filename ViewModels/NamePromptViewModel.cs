using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

/// <summary>
/// Simple ViewModel for a dialog prompting the user for a text input (e.g., naming a folder).
/// </summary>
public partial class NamePromptViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _message;

    [ObservableProperty]
    private string _inputText = string.Empty;

    public NamePromptViewModel(string title, string message)
    {
        Title = title;
        Message = message;

        // Commands for dialog result handling
        // These commands typically close the window, returning true or false to the caller.
        OkCommand = new RelayCommand<Avalonia.Controls.Window>(win => win?.Close(true));
        CancelCommand = new RelayCommand<Avalonia.Controls.Window>(win => win?.Close(false));
    }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }
}