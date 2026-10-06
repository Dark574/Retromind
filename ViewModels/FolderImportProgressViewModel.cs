using System;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Services;

namespace Retromind.ViewModels;

public sealed partial class FolderImportProgressViewModel : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    [ObservableProperty]
    private string _statusText;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;

    public FolderImportProgressViewModel()
    {
        _statusText = T("FolderImport.Scanning", "Scanning the selected folder...");
        CancelCommand = new RelayCommand(RequestCancel, CanCancel);
    }

    public string Title => T("FolderImport.Title", "Import ROM folder");
    public string Description => T(
        "FolderImport.Description",
        "Retromind is finding matching files and existing media. Nothing is added until this step finishes.");
    public string CancelText => T("Button.Cancel", "Cancel");
    public IRelayCommand CancelCommand { get; }
    public CancellationToken Token => _cancellationTokenSource.Token;

    public void Report(FolderImportProgress progress)
    {
        if (_disposed || IsCancelling)
            return;

        switch (progress.Stage)
        {
            case FolderImportStage.ScanningFiles:
                StatusText = T("FolderImport.Scanning", "Scanning the selected folder...");
                DetailText = string.Format(
                    CultureInfo.CurrentCulture,
                    T("FolderImport.ScanningFormat", "{0:N0} files checked, {1:N0} matching files found."),
                    progress.FilesScanned,
                    progress.MatchingFileCount);
                break;

            case FolderImportStage.ReadingCueFiles:
                StatusText = T("FolderImport.ReadingCue", "Reading CUE files...");
                DetailText = string.Format(
                    CultureInfo.CurrentCulture,
                    T("FolderImport.ReadingCueFormat", "{0:N0} of {1:N0} CUE files read."),
                    progress.CompletedCount,
                    progress.TotalCount);
                break;

            case FolderImportStage.PreparingItems:
                StatusText = T("FolderImport.Preparing", "Preparing imported items...");
                DetailText = string.Format(
                    CultureInfo.CurrentCulture,
                    T("FolderImport.PreparingFormat", "{0:N0} of {1:N0} matching files prepared."),
                    progress.CompletedCount,
                    progress.TotalCount);
                break;

            case FolderImportStage.MatchingAssets:
                StatusText = T("FolderImport.MatchingAssets", "Matching existing media...");
                DetailText = string.Format(
                    CultureInfo.CurrentCulture,
                    T("FolderImport.MatchingAssetsFormat", "{0:N0} media files checked, {1:N0} assigned."),
                    progress.FilesScanned,
                    progress.MatchingFileCount);
                break;
        }
    }

    public void RequestCancel()
    {
        if (!CanCancel())
            return;

        IsCancelling = true;
        StatusText = T("FolderImport.Cancelling", "Cancelling import...");
        DetailText = T(
            "FolderImport.CancellingDescription",
            "The library will remain unchanged.");
        _cancellationTokenSource.Cancel();
    }

    public void MarkFinished()
    {
        IsRunning = false;
    }

    private bool CanCancel() => IsRunning && !IsCancelling && !_disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_cancellationTokenSource.IsCancellationRequested)
            _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }
}
