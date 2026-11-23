using System.Collections.Generic;
using System.Collections.Immutable;
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
    private volatile IReadOnlySet<(string ItemId, string? StoreGameId)> _pendingGogInstallations =
        ImmutableHashSet<(string, string?)>.Empty;

    public bool HasDownloadRecovery => _hasDownloadRecovery;
    public IReadOnlySet<(string ItemId, string? StoreGameId)> PendingGogInstallations => _pendingGogInstallations;
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
        if (status.Activity.GetValueOrDefault() != _downloadActivity || status.NeedsRepair != _hasDownloadRecovery ||
            !ReferenceEquals(status.PendingGogInstallations, _pendingGogInstallations))
            UiThreadHelper.Post(RefreshDownloadQueueStatus);
    }

    private void RefreshDownloadQueueStatus()
    {
        var status = _downloads.GetQueueStatus();
        var activity = status.Activity.GetValueOrDefault();
        if (activity != _downloadActivity || status.NeedsRepair != _hasDownloadRecovery)
        {
            _downloadActivity = activity;
            _hasDownloadRecovery = status.NeedsRepair;
            OnPropertyChanged(nameof(IsDownloadManagerActive));
            OnPropertyChanged(nameof(HasDownloadRecovery));
            OnPropertyChanged(nameof(DownloadManagerToolTip));
        }

        if (!ReferenceEquals(status.PendingGogInstallations, _pendingGogInstallations))
        {
            _pendingGogInstallations = status.PendingGogInstallations;
            OnPropertyChanged(nameof(PendingGogInstallations));
        }
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
