using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores;

namespace Retromind.ViewModels;

public sealed class DownloadManagerViewModel : ViewModelBase, IDisposable
{
    private readonly DownloadManagerService _manager;
    private readonly Func<string, string?> _title;
    private readonly Func<DownloadStore?> _selectedStore;
    private readonly MediaItem? _game;
    private readonly Action<MediaItem, bool> _setExcluded;
    private readonly Func<MediaItem, bool> _isExcluded;
    private int _refreshPosted;
    private bool _disposed;
    private Entry? _selected;

    internal DownloadManagerViewModel(DownloadManagerService manager, Func<string, string?> title, MediaItem? game,
        Func<DownloadStore?> selectedStore, Func<MediaItem, bool> isExcluded, Action<MediaItem, bool> setExcluded,
        Func<string, Task<bool>> confirmDiscardRepair)
    {
        _manager = manager;
        _title = title;
        _game = game;
        _selectedStore = selectedStore;
        _isExcluded = isExcluded;
        _setExcluded = setExcluded;

        AddUpdate = new RelayCommand(() => Enqueue(DownloadKind.Update), () => _selectedStore() != null && !QueueUnavailable);
        AddWindowsInstaller = new RelayCommand(() => Enqueue(DownloadKind.OfflineInstaller, "windows"),
            () => _selectedStore() == DownloadStore.Gog && !QueueUnavailable);
        AddLinuxInstaller = new RelayCommand(() => Enqueue(DownloadKind.OfflineInstaller, "linux"),
            () => _selectedStore() == DownloadStore.Gog && !QueueUnavailable);
        ToggleQueuePause = new RelayCommand(_manager.ToggleQueuePause, () => !QueueUnavailable);
        Start = Create(DownloadAction.Start, DownloadState.Queued, DownloadState.WaitingForWindow, DownloadState.WaitingForSpace,
            DownloadState.WaitingForGame, DownloadState.Paused);
        Pause = Create(DownloadAction.Pause, DownloadState.Queued, DownloadState.WaitingForWindow,
            DownloadState.WaitingForSpace, DownloadState.WaitingForGame, DownloadState.Downloading);
        Resume = Create(DownloadAction.Resume, DownloadState.Paused);
        Cancel = Create(DownloadAction.Cancel, DownloadState.Queued, DownloadState.WaitingForWindow,
            DownloadState.WaitingForSpace, DownloadState.WaitingForGame, DownloadState.Downloading,
            DownloadState.Paused, DownloadState.InstallationPending, DownloadState.Failed);
        Retry = Create(DownloadAction.Retry, DownloadState.Failed, DownloadState.WaitingForSpace, DownloadState.WaitingForGame);
        Install = Create(DownloadAction.Install, DownloadState.InstallationPending);
        Repair = Create(DownloadAction.Repair, DownloadState.RepairRequired);
        Earlier = Create(DownloadAction.Earlier, DownloadState.Queued, DownloadState.Paused, DownloadState.WaitingForWindow,
            DownloadState.WaitingForGame, DownloadState.WaitingForSpace);
        Later = Create(DownloadAction.Later, DownloadState.Queued, DownloadState.Paused, DownloadState.WaitingForWindow,
            DownloadState.WaitingForGame, DownloadState.WaitingForSpace);
        Remove = new AsyncRelayCommand(() => RemoveAsync(confirmDiscardRepair),
            () => !QueueUnavailable && Selected?.Job.State is
                DownloadState.Completed or DownloadState.Cancelled or DownloadState.RepairRequired);
        _manager.Changed += OnQueueChanged;
        Refresh();
    }

    public ObservableCollection<Entry> Entries { get; } = [];
    public bool IsEmpty => Entries.Count == 0 && !QueueUnavailable;
    public bool QueueUnavailable => _manager.PersistenceFailed;
    public string QueuePauseLabel => _manager.IsQueuePaused
        ? Retromind.Resources.DownloadStrings.ResumeQueue
        : Retromind.Resources.DownloadStrings.PauseQueue;
    public bool HasSelectedGame => _game != null && _selectedStore() != null;
    public string? SelectedGameTitle => _game?.Title;
    public bool Excluded
    {
        get => _game != null && _isExcluded(_game);
        set
        {
            if (_game != null)
                _setExcluded(_game, value);
        }
    }

    public Entry? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
                RefreshCommands();
        }
    }

    public IRelayCommand AddUpdate { get; }
    public IRelayCommand AddWindowsInstaller { get; }
    public IRelayCommand AddLinuxInstaller { get; }
    public IRelayCommand ToggleQueuePause { get; }
    public IRelayCommand Start { get; }
    public IRelayCommand Pause { get; }
    public IRelayCommand Resume { get; }
    public IRelayCommand Cancel { get; }
    public IRelayCommand Retry { get; }
    public IRelayCommand Earlier { get; }
    public IRelayCommand Later { get; }
    public IRelayCommand Install { get; }
    public IRelayCommand Repair { get; }
    public IAsyncRelayCommand Remove { get; }

    private async Task RemoveAsync(Func<string, Task<bool>> confirmDiscardRepair)
    {
        if (Selected is not { } selected)
            return;

        var id = selected.Job.Id;
        if (selected.Job.State == DownloadState.RepairRequired)
        {
            var format = T("Downloads.DiscardRepairConfirmationFormat",
                "Dismiss the repair notice for ‘{0}’? The installation may still be incomplete. Game files and downloads will be kept.");
            var message = string.Format(CultureInfo.CurrentCulture, format, selected.Title);
            if (await confirmDiscardRepair(message))
                _manager.Change(id, DownloadAction.DiscardRepair);
            return;
        }

        _manager.Change(id, DownloadAction.Remove);
    }

    private IRelayCommand Create(DownloadAction action, params DownloadState[] allowed) =>
        new RelayCommand(() => { if (Selected is { } selected) _manager.Change(selected.Job.Id, action); },
            () => !QueueUnavailable && (action != DownloadAction.Start || !_manager.IsQueuePaused) &&
                  Selected is { } selected && allowed.Contains(selected.Job.State));

    private void Enqueue(DownloadKind kind, string? platform = null)
    {
        if (_game == null || _selectedStore() is not { } store)
            return;

        var gameId = store == DownloadStore.Gog
            ? GogMediaItemStateHelper.TryGetGameId(_game)
            : EpicMediaItemStateHelper.TryGetGameId(_game);
        if (gameId != null)
            _manager.Enqueue(_game.Id, store, kind, platform: platform, storeGameId: gameId);
    }

    private void OnQueueChanged()
    {
        if (Interlocked.Exchange(ref _refreshPosted, 1) != 0)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            if (!_disposed)
                Refresh();
        });
    }

    private void Refresh()
    {
        var selectedId = Selected?.Job.Id;
        var snapshot = _manager.Snapshot();
        var existing = Entries.ToDictionary(entry => entry.Job.Id);
        var ids = snapshot.Select(row => row.Job.Id).ToHashSet();
        for (var index = Entries.Count - 1; index >= 0; index--)
        {
            if (!ids.Contains(Entries[index].Job.Id))
                Entries.RemoveAt(index);
        }

        for (var index = 0; index < snapshot.Length; index++)
        {
            var (job, progress) = snapshot[index];
            if (existing.TryGetValue(job.Id, out var entry))
            {
                entry.Refresh(job, progress);
                if (!ReferenceEquals(Entries[index], entry))
                    Entries.Move(Entries.IndexOf(entry), index);
            }
            else
            {
                Entries.Insert(index, new Entry(job,
                    _title(job.ItemId) ?? T("Downloads.UnavailableGame", "Unavailable game"), progress));
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(QueueUnavailable));
        OnPropertyChanged(nameof(QueuePauseLabel));
        Selected = Entries.FirstOrDefault(entry => entry.Job.Id == selectedId) ?? Entries.FirstOrDefault();
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { Start, Pause, Resume, Cancel, Retry, Earlier, Later, Install, Repair, Remove,
                     AddUpdate, AddWindowsInstaller, AddLinuxInstaller, ToggleQueuePause })
            command.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _manager.Changed -= OnQueueChanged;
    }

    public sealed class Entry : ViewModelBase
    {
        private DownloadProgress _progress;
        internal Entry(DownloadJob job, string title, DownloadProgress progress)
        {
            Job = job;
            Title = title;
            _progress = progress;
        }

        internal void Refresh(DownloadJob job, DownloadProgress progress)
        {
            if (Job == job && _progress == progress)
                return;

            Job = job;
            _progress = progress;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Reason));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(Indeterminate));
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(Data));
            OnPropertyChanged(nameof(Speed));
        }

        internal DownloadJob Job { get; private set; }
        public string Title { get; }
        public string Store => Job.Store == DownloadStore.Gog ? "GOG" : "Epic Games";
        public string Kind => Job.Kind == DownloadKind.OfflineInstaller
            ? T("Downloads.OfflineInstaller", "Offline installer") + (Job.Platform == "windows" ? " · Windows" : " · Linux")
            : Job.RepairRequested ? Retromind.Resources.DownloadStrings.Repair
                : Job.InstallRequested ? Retromind.Resources.DownloadStrings.Install
                : T("Downloads.Update", "Update");
        public string Status => T("Downloads.State." + Job.State, Job.State.ToString());
        public string? Reason => Job.Reason == DownloadReason.None ? null : T("Downloads.Reason." + Job.Reason, Job.Reason.ToString());
        public bool IsActive => Job.State is DownloadState.Installing or DownloadState.Downloading;
        public bool Indeterminate => IsActive && !_progress.Fraction.HasValue && !_progress.Total.HasValue;
        public double Percent => Math.Clamp(100d * (_progress.Fraction ??
            (_progress.Total is > 0 ? (double)_progress.Bytes / _progress.Total.Value : Job.PayloadReady ? 1 : 0)), 0, 100);
        public string Data => _progress.Total is { } total
            ? StoreDiskSpaceHelper.FormatByteSize(_progress.Bytes) + " / " + StoreDiskSpaceHelper.FormatByteSize(total)
            : _progress.Bytes > 0 ? StoreDiskSpaceHelper.FormatByteSize(_progress.Bytes) : string.Empty;
        public string? Speed => IsActive && _progress.BytesPerSecond is { } speed
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB/s", speed / 1_000_000)
            : null;
    }
}
