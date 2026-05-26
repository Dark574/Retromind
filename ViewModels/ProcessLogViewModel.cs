using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

/// <summary>
/// Simple log view model for long-running external processes.
/// </summary>
public partial class ProcessLogViewModel : ViewModelBase
{
    private readonly bool _newestFirst;
    public bool NewestFirst => _newestFirst;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isRunning = true;

    public string StatusText => IsRunning ? "Running..." : "Finished.";

    public IRelayCommand<Avalonia.Controls.Window?> CloseCommand { get; }

    public ProcessLogViewModel(string title, bool newestFirst = false)
    {
        Title = title;
        _newestFirst = newestFirst;
        CloseCommand = new RelayCommand<Avalonia.Controls.Window?>(win => win?.Close(), _ => !IsRunning);
    }

    public void AppendLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (string.IsNullOrEmpty(LogText))
        {
            LogText = line;
            return;
        }

        LogText = _newestFirst
            ? line + Environment.NewLine + LogText
            : LogText + Environment.NewLine + line;
    }
}
