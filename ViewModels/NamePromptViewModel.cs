using System.Reactive;
using ReactiveUI;

namespace Retromind.ViewModels;

public class NamePromptViewModel : ReactiveObject
{
    public NamePromptViewModel(string title, string message)
    {
        Title = title;
        Message = message;

        OkCommand = ReactiveCommand.Create(() => true);
        CancelCommand = ReactiveCommand.Create(() => false);
    }

    public string Title { get; }
    public string Message { get; }
    public string InputText { get; set; } = "";

    public ReactiveCommand<Unit, bool> OkCommand { get; }
    public ReactiveCommand<Unit, bool> CancelCommand { get; }
}