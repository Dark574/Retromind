using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

/// <summary>
/// Simple log view model for long-running external processes.
/// </summary>
public partial class ProcessLogViewModel : ViewModelBase, IDisposable
{
    private readonly bool _newestFirst;
    public bool NewestFirst => _newestFirst;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isRunning = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancel;

    [ObservableProperty]
    private bool _isCancelled;

    public string StatusText => IsCancelled
        ? "Cancelled."
        : IsRunning ? "Running..." : "Finished.";

    public IRelayCommand<Avalonia.Controls.Window?> CloseCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public ProcessLogViewModel(string title, bool newestFirst = false)
    {
        Title = title;
        _newestFirst = newestFirst;
        _cts = new CancellationTokenSource();
        
        CloseCommand = new RelayCommand<Avalonia.Controls.Window?>(win => win?.Close());
        CancelCommand = new RelayCommand(CancelOperation, CanCancelOperation);
    }

    private void CancelOperation()
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            IsCancelled = true;
            CanCancel = false;
            AppendLine("Cancellation requested by user...");
        }
    }

    private bool CanCancelOperation() => CanCancel && !IsCancelled;

    /// <summary>
    /// Returns the CancellationToken for the current operation.
    /// </summary>
    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>
    /// Enables the cancel button for an operation that supports cancellation.
    /// </summary>
    public void EnableCancel()
    {
        CanCancel = true;
    }

    /// <summary>
    /// Marks the operation as finished (success or failure), disabling cancel.
    /// </summary>
    public void MarkFinished()
    {
        CanCancel = false;
        DisposeCts();
    }

    /// <summary>
    /// Marks the operation as canceled by the user.
    /// </summary>
    public void MarkCancelled(string? message = null)
    {
        IsCancelled = true;
        CanCancel = false;
        if (!string.IsNullOrWhiteSpace(message))
            AppendLine(message);
        DisposeCts();
    }

    private void DisposeCts()
    {
        if (_cts is not null)
        {
            _cts.Dispose();
            _cts = null;
        }
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        DisposeCts();
    }
}