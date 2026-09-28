using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public sealed record LibraryHealthFilterOption(LibraryHealthIssueKind? Kind, string Label);

public sealed record LibraryHealthIssueRow(
    LibraryHealthIssue Issue,
    string TypeText,
    string OwnerText,
    string NodePathText,
    string LocationText,
    string SizeText);

public sealed partial class LibraryHealthViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryHealthService _service;
    private readonly IReadOnlyList<MediaNode> _rootNodes;
    private readonly List<LibraryHealthIssueRow> _allRows = new();
    private CancellationTokenSource? _scanCts;
    private LibraryHealthReport? _report;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyReportCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private LibraryHealthFilterOption _selectedFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResult))]
    private bool _hasReport;

    public ObservableCollection<LibraryHealthIssueRow> VisibleIssues { get; } = new();
    public IReadOnlyList<LibraryHealthFilterOption> FilterOptions { get; }

    public string Title => T("LibraryHealth.Title", "Library check");
    public string Description => T(
        "LibraryHealth.Description",
        "Checks library references and Retromind-managed media folders without changing or deleting anything.");
    public string ScanText => T("LibraryHealth.Scan", "Check again");
    public string CancelText => T("Button_Cancel", "Cancel");
    public string CopyReportText => T("LibraryHealth.CopyReport", "Copy report");
    public string CloseText => T("Button_Close", "Close");
    public string TypeHeader => T("LibraryHealth.Header.Type", "Problem");
    public string OwnerHeader => T("LibraryHealth.Header.Owner", "Item / category");
    public string NodeHeader => T("LibraryHealth.Header.Node", "Library path");
    public string LocationHeader => T("LibraryHealth.Header.Location", "File / value");
    public string SizeHeader => T("LibraryHealth.Header.Size", "Size");
    public string MissingAssetsTitle => T("LibraryHealth.Summary.MissingAssets", "Missing media");
    public string OrphanedAssetsTitle => T("LibraryHealth.Summary.OrphanedAssets", "Orphaned media");
    public string MissingLaunchFilesTitle => T("LibraryHealth.Summary.MissingLaunchFiles", "Missing launch files");
    public string InconsistentEntriesTitle => T("LibraryHealth.Summary.InconsistentEntries", "Inconsistent entries");
    public string EmptyResultText => T("LibraryHealth.Empty", "No problems were found in the checked areas.");
    public string ScopeHint => T(
        "LibraryHealth.ScopeHint",
        "Orphan detection is limited to the managed media folders of existing categories. Game folders, prefixes, installers, themes, caches and backups are never scanned as orphaned media.");

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool ShowEmptyResult => HasReport && !IsScanning && _allRows.Count == 0;
    public int MissingAssetCount => Count(LibraryHealthIssueKind.MissingAsset);
    public int OrphanedAssetCount => Count(LibraryHealthIssueKind.OrphanedAsset);
    public int MissingLaunchFileCount => Count(LibraryHealthIssueKind.MissingLaunchFile);
    public int InconsistentEntryCount => Count(LibraryHealthIssueKind.InconsistentEntry);
    public string OrphanedAssetSizeText => FormatSize(_report?.Issues
        .Where(static issue => issue.Kind == LibraryHealthIssueKind.OrphanedAsset)
        .Sum(static issue => issue.SizeBytes ?? 0) ?? 0);

    public IAsyncRelayCommand ScanCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand CopyReportCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Func<string, Task>? RequestCopy;
    public event Action? RequestClose;

    public LibraryHealthViewModel(
        LibraryHealthService service,
        IEnumerable<MediaNode> rootNodes)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _rootNodes = rootNodes?.ToArray() ?? throw new ArgumentNullException(nameof(rootNodes));
        FilterOptions =
        [
            new(null, T("LibraryHealth.Filter.All", "All findings")),
            new(LibraryHealthIssueKind.MissingAsset, T("LibraryHealth.Filter.MissingAssets", "Missing media")),
            new(LibraryHealthIssueKind.OrphanedAsset, T("LibraryHealth.Filter.OrphanedAssets", "Orphaned media")),
            new(LibraryHealthIssueKind.MissingLaunchFile, T("LibraryHealth.Filter.MissingLaunchFiles", "Missing launch files")),
            new(LibraryHealthIssueKind.InconsistentEntry, T("LibraryHealth.Filter.InconsistentEntries", "Inconsistent entries"))
        ];
        _selectedFilter = FilterOptions[0];

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
        CopyReportCommand = new AsyncRelayCommand(CopyReportAsync, () => HasReport && !IsScanning);
        CloseCommand = new RelayCommand(Close);
    }

    public async Task LoadAsync()
    {
        if (!HasReport && !IsScanning)
            await ScanAsync();
    }

    partial void OnSelectedFilterChanged(LibraryHealthFilterOption value) => ApplyFilter();

    partial void OnHasReportChanged(bool value)
    {
        CopyReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowEmptyResult));
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyResult));
    }

    private async Task ScanAsync()
    {
        if (_disposed || IsScanning)
            return;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = T("LibraryHealth.Scanning", "Checking library...");

        try
        {
            _report = await _service.ScanAsync(_rootNodes, _scanCts.Token);
            BuildRows(_report);
            HasReport = true;
            StatusText = string.Format(
                CultureInfo.CurrentCulture,
                T("LibraryHealth.CompletedFormat", "Checked {0:N0} categories, {1:N0} items, {2:N0} media references and {3:N0} media files."),
                _report.NodeCount,
                _report.ItemCount,
                _report.AssetReferenceCount,
                _report.AssetFileCount);
        }
        catch (OperationCanceledException)
        {
            StatusText = T("LibraryHealth.Cancelled", "Library check cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = string.Format(
                CultureInfo.CurrentCulture,
                T("LibraryHealth.FailedFormat", "Library check failed: {0}"),
                ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void BuildRows(LibraryHealthReport report)
    {
        _allRows.Clear();
        _allRows.AddRange(report.Issues.Select(issue => new LibraryHealthIssueRow(
            issue,
            GetIssueText(issue.Reason),
            string.IsNullOrWhiteSpace(issue.Owner) ? "–" : issue.Owner,
            string.IsNullOrWhiteSpace(issue.NodePath) ? "–" : issue.NodePath,
            string.IsNullOrWhiteSpace(issue.Location) ? "–" : issue.Location,
            issue.SizeBytes.HasValue ? FormatSize(issue.SizeBytes.Value) : "–")));

        ApplyFilter();
        OnPropertyChanged(nameof(MissingAssetCount));
        OnPropertyChanged(nameof(OrphanedAssetCount));
        OnPropertyChanged(nameof(MissingLaunchFileCount));
        OnPropertyChanged(nameof(InconsistentEntryCount));
        OnPropertyChanged(nameof(OrphanedAssetSizeText));
        OnPropertyChanged(nameof(ShowEmptyResult));
    }

    private void ApplyFilter()
    {
        var rows = SelectedFilter.Kind.HasValue
            ? _allRows.Where(row => row.Issue.Kind == SelectedFilter.Kind.Value)
            : _allRows;
        VisibleIssues.Clear();
        foreach (var row in rows)
            VisibleIssues.Add(row);
    }

    private int Count(LibraryHealthIssueKind kind) =>
        _report?.Issues.Count(issue => issue.Kind == kind) ?? 0;

    private async Task CopyReportAsync()
    {
        if (_report == null || RequestCopy == null)
            return;

        var builder = new StringBuilder();
        builder.AppendLine(Title);
        builder.AppendLine(StatusText);
        builder.AppendLine();
        foreach (var row in _allRows)
        {
            builder.Append(row.TypeText).Append(" | ")
                .Append(row.NodePathText).Append(" | ")
                .Append(row.OwnerText).Append(" | ")
                .Append(row.LocationText);
            if (row.Issue.SizeBytes.HasValue)
                builder.Append(" | ").Append(row.SizeText);
            builder.AppendLine();
        }

        await RequestCopy(builder.ToString());
        StatusText = T("LibraryHealth.Copied", "The report was copied to the clipboard.");
    }

    private string GetIssueText(LibraryHealthIssueReason reason) => reason switch
    {
        LibraryHealthIssueReason.AssetFileMissing => T("LibraryHealth.Reason.AssetFileMissing", "Referenced media file is missing"),
        LibraryHealthIssueReason.OrphanedAssetFile => T("LibraryHealth.Reason.OrphanedAssetFile", "Media file is not referenced"),
        LibraryHealthIssueReason.LaunchFileMissing => T("LibraryHealth.Reason.LaunchFileMissing", "Launch file is missing"),
        LibraryHealthIssueReason.EmptyAssetPath => T("LibraryHealth.Reason.EmptyAssetPath", "Media reference has an empty path"),
        LibraryHealthIssueReason.InvalidAssetPath => T("LibraryHealth.Reason.InvalidAssetPath", "Media path is invalid or outside the data folder"),
        LibraryHealthIssueReason.DuplicateAssetReference => T("LibraryHealth.Reason.DuplicateAssetReference", "Duplicate media reference"),
        LibraryHealthIssueReason.EmptyLaunchPath => T("LibraryHealth.Reason.EmptyLaunchPath", "Launch reference has an empty path"),
        LibraryHealthIssueReason.InvalidLaunchPath => T("LibraryHealth.Reason.InvalidLaunchPath", "Launch path is invalid"),
        LibraryHealthIssueReason.MissingNodeId => T("LibraryHealth.Reason.MissingNodeId", "Category ID is missing"),
        LibraryHealthIssueReason.DuplicateNodeId => T("LibraryHealth.Reason.DuplicateNodeId", "Category ID is duplicated"),
        LibraryHealthIssueReason.MissingItemId => T("LibraryHealth.Reason.MissingItemId", "Item ID is missing"),
        LibraryHealthIssueReason.DuplicateItemId => T("LibraryHealth.Reason.DuplicateItemId", "Item ID is duplicated"),
        LibraryHealthIssueReason.AssetFolderUnreadable => T("LibraryHealth.Reason.AssetFolderUnreadable", "Media folder could not be scanned safely"),
        _ => reason.ToString()
    };

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private void CancelScan() => _scanCts?.Cancel();

    private void Close()
    {
        CancelScan();
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
    }
}
