using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Resources;
using Retromind.Services.Downloads;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool _closingDownloads;
    private bool _downloadsReady;
    private volatile bool _hasDownloadRecovery;
    private volatile DownloadState _downloadActivity;

    public bool HasDownloadRecovery => _hasDownloadRecovery;
    public bool IsDownloadManagerActive => _downloadActivity is DownloadState.Downloading or DownloadState.Installing;
    public string DownloadManagerToolTip => _downloadActivity switch
    {
        DownloadState.Downloading => T("Downloads.State.Downloading", "Preparing or downloading"),
        DownloadState.Installing => T("Downloads.State.Installing", "Installing"),
        _ when HasDownloadRecovery =>
            T("Downloads.RecoveryAvailable", "A game installation may need repair. Open the download manager."),
        _ => DownloadStrings.Title
    };

    private void OnDownloadQueueChanged()
    {
        var status = _downloads.GetQueueStatus();
        if (status.Activity.GetValueOrDefault() != _downloadActivity || status.NeedsRepair != _hasDownloadRecovery)
            UiThreadHelper.Post(RefreshDownloadQueueStatus);
    }

    private void RefreshDownloadQueueStatus()
    {
        var status = _downloads.GetQueueStatus();
        var activity = status.Activity.GetValueOrDefault();
        if (activity == _downloadActivity && status.NeedsRepair == _hasDownloadRecovery)
            return;

        _downloadActivity = activity;
        _hasDownloadRecovery = status.NeedsRepair;
        OnPropertyChanged(nameof(IsDownloadManagerActive));
        OnPropertyChanged(nameof(HasDownloadRecovery));
        OnPropertyChanged(nameof(DownloadManagerToolTip));
    }

    internal bool HasStoreActivity => _downloads.HasStoreActivity;

    [RelayCommand]
    private void OpenDownloads() => _downloads.ShowDownloads(GetCurrentSelectedItem());

    internal void ResumeDownloadsAfterCancelledClose()
    {
        _closingDownloads = false;
        if (_downloadsReady && !_metadataBackups.RequiresRestart)
            _downloads.Start();
    }
}
