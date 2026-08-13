using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.Scrapers;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the manual scraping dialog for a single item.
/// Allows the user to select a scraper service, enter a query, and pick a result.
/// </summary>
public partial class ScrapeDialogViewModel : ViewModelBase, IDisposable
{
    private const int MaxResults = 40;

    private readonly MetadataService _metadataService;
    private readonly MediaItem _targetItem;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewEnrichmentCts;
    private Task _previewEnrichmentTask = Task.CompletedTask;
    private readonly HashSet<ScraperSearchResult> _enrichedResults = new();
    private bool _disposed;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ScraperConfig? _selectedScraper;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isPreviewBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ScraperSearchResult? _selectedResult;

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();
    public ObservableCollection<ScrapeMetadataChoice> MetadataChoices { get; } = new();
    public ObservableCollection<ScrapeArtworkChoice> ArtworkChoices { get; } = new();

    // Bulk-update friendly collection (prevents UI stalls when a provider returns many results).
    public RangeObservableCollection<ScraperSearchResult> SearchResults { get; } = new();

    private ScraperImportSettings ImportSettings =>
        _settings.ScraperImport ??= new ScraperImportSettings();

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    public string NoCoverText =>
        Strings.ResourceManager.GetString("Metadata.NoCover", Strings.Culture) ?? "No cover";
    public string MetadataSelectionTitle => T("ScrapeDialog.MetadataSelectionTitle", "Select metadata");
    public string MetadataSelectionHint => T(
        "ScrapeDialog.MetadataSelectionHint",
        "Select each value you want to copy. Existing values are only changed when selected.");
    public string ExistingValueText => T("ScrapeDialog.ExistingValue", "Existing");
    public string ScraperValueText => T("ScrapeDialog.ScraperValue", "Scraper");
    public string ArtworkSelectionTitle => T("ScrapeDialog.ArtworkSelectionTitle", "Add artwork");
    public string ArtworkSelectionHint => T(
        "ScrapeDialog.ArtworkSelectionHint",
        "Selected artwork is added as another variant. Existing artwork is never replaced.");
    public string SelectAllText => T("ScrapeDialog.SelectAll", "Select all");
    public string ClearSelectionText => T("ScrapeDialog.ClearSelection", "Clear selection");

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IRelayCommand SelectAllMetadataCommand { get; }
    public IRelayCommand ClearMetadataSelectionCommand { get; }
    public IRelayCommand SelectAllArtworkCommand { get; }
    public IRelayCommand ClearArtworkSelectionCommand { get; }

    public event Func<ScraperSearchResult, Task>? OnResultSelectedAsync;

    public ScrapeDialogViewModel(MediaItem item, AppSettings settings, MetadataService metadataService)
    {
        _targetItem = item ?? throw new ArgumentNullException(nameof(item));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => SelectedResult != null && !IsPreviewBusy);
        SelectAllMetadataCommand = new RelayCommand(
            () => SetMetadataSelection(true),
            () => MetadataChoices.Count > 0);
        ClearMetadataSelectionCommand = new RelayCommand(
            () => SetMetadataSelection(false),
            () => MetadataChoices.Count > 0);
        SelectAllArtworkCommand = new RelayCommand(
            () => SetArtworkSelection(true),
            () => ArtworkChoices.Count > 0);
        ClearArtworkSelectionCommand = new RelayCommand(
            () => SetArtworkSelection(false),
            () => ArtworkChoices.Count > 0);

        // Selecting the initial scraper triggers command-state updates, so all
        // commands must exist before InitializeData assigns SelectedScraper.
        InitializeData();
    }

    private void InitializeData()
    {
        SearchQuery = _targetItem.Title ?? string.Empty;

        AvailableScrapers.Clear();
        foreach (var s in _settings.Scrapers.Where(s => s.Type != ScraperType.None && s.Type != ScraperType.EmuMovies))
            AvailableScrapers.Add(s);

        SelectedScraper = AvailableScrapers.Count > 0 ? AvailableScrapers[0] : null;
    }

    private async Task SearchAsync()
    {
        if (_disposed)
            return;

        if (SelectedScraper == null || string.IsNullOrWhiteSpace(SearchQuery))
            return;

        // Cancel previous search (avoid out-of-order results + unnecessary traffic).
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsBusy = true;
        SearchResults.Clear();
        SelectedResult = null;
        MetadataChoices.Clear();
        ArtworkChoices.Clear();
        RefreshChoiceCommandStates();
        _enrichedResults.Clear();
        StatusMessage = string.Empty;

        try
        {
            var provider = await _metadataService.GetProviderAsync(SelectedScraper.Id, token);
            if (provider == null)
            {
                StatusMessage = Strings.Metadata_Error_ProviderNotAvailable;
                return;
            }

            var results = await provider.SearchAsync(SearchQuery, token);
            token.ThrowIfCancellationRequested();

            if (results.Count == 0)
            {
                StatusMessage = Strings.Metadata_Search_NoResults;
                return;
            }

            var limited = results.Take(MaxResults).ToList();
            if (provider is IMetadataSearchPreviewEnricher previewEnricher)
                await previewEnricher.EnrichPreviewsAsync(limited, token);

            token.ThrowIfCancellationRequested();
            await UiThreadHelper.InvokeAsync(() => SearchResults.ReplaceAll(limited));
        }
        catch (OperationCanceledException)
        {
            // Expected when the user searches again quickly; keep UI quiet.
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
            if (_disposed)
            {
                _searchCts?.Dispose();
                _searchCts = null;
            }
        }
    }

    partial void OnSelectedScraperChanged(ScraperConfig? value)
    {
        CancelPreviewEnrichment();
        _enrichedResults.Clear();
        MetadataChoices.Clear();
        ArtworkChoices.Clear();
        RefreshChoiceCommandStates();
        IsPreviewBusy = false;
    }

    partial void OnSelectedResultChanged(ScraperSearchResult? value)
    {
        CancelPreviewEnrichment();
        IsPreviewBusy = false;
        RebuildImportChoices(value);

        if (_disposed ||
            value == null ||
            SelectedScraper == null ||
            _enrichedResults.Contains(value))
        {
            return;
        }

        if (_metadataService.GetProvider(SelectedScraper.Id) is not IMetadataResultEnricher)
            return;

        StatusMessage = string.Empty;
        var cts = new CancellationTokenSource();
        _previewEnrichmentCts = cts;
        IsPreviewBusy = true;
        _previewEnrichmentTask = EnrichSelectedPreviewAsync(value, SelectedScraper.Id, cts);
    }

    private async Task EnrichSelectedPreviewAsync(
        ScraperSearchResult result,
        string scraperId,
        CancellationTokenSource cts)
    {
        try
        {
            var provider = await _metadataService.GetProviderAsync(scraperId, cts.Token);
            if (provider is not IMetadataResultEnricher enricher)
                return;

            await enricher.EnrichAsync(result, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            if (!_disposed &&
                ReferenceEquals(_previewEnrichmentCts, cts) &&
                ReferenceEquals(SelectedResult, result) &&
                string.Equals(SelectedScraper?.Id, scraperId, StringComparison.Ordinal))
            {
                _enrichedResults.Add(result);

                // ScraperSearchResult is a lightweight DTO rather than an observable
                // model. Notify the parent binding so nested image URLs are reevaluated.
                OnPropertyChanged(nameof(SelectedResult));
                RebuildImportChoices(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user quickly selects another result.
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_previewEnrichmentCts, cts))
                StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_previewEnrichmentCts, cts))
            {
                _previewEnrichmentCts = null;
                IsPreviewBusy = false;
            }

            cts.Dispose();
        }
    }

    private void CancelPreviewEnrichment()
    {
        var cts = _previewEnrichmentCts;
        _previewEnrichmentCts = null;
        cts?.Cancel();
    }

    private async Task StopPreviewEnrichmentAsync()
    {
        var pendingTask = _previewEnrichmentTask;
        CancelPreviewEnrichment();

        try
        {
            await pendingTask;
        }
        catch (OperationCanceledException)
        {
            // Expected while switching from preview loading to apply.
        }
        finally
        {
            _previewEnrichmentTask = Task.CompletedTask;
            IsPreviewBusy = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (_disposed || SelectedResult == null)
            return;

        var handler = OnResultSelectedAsync;
        if (handler == null)
            return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var selectedResult = SelectedResult;
            await StopPreviewEnrichmentAsync();

            if (SelectedScraper != null)
            {
                var provider = await _metadataService.GetProviderAsync(SelectedScraper.Id);
                if (provider is IMetadataResultEnricher enricher &&
                    !_enrichedResults.Contains(selectedResult))
                {
                    await enricher.EnrichAsync(selectedResult);
                    _enrichedResults.Add(selectedResult);
                    RebuildImportChoices(selectedResult);
                }
            }

            var filteredResult = BuildSelectedResult(selectedResult);
            foreach (var subscriber in handler.GetInvocationList())
            {
                if (subscriber is Func<ScraperSearchResult, Task> callback)
                    await callback(filteredResult);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing or a provider operation was cancelled.
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildImportChoices(ScraperSearchResult? result)
    {
        MetadataChoices.Clear();
        ArtworkChoices.Clear();

        if (result == null)
        {
            RefreshChoiceCommandStates();
            return;
        }

        var settings = ImportSettings;
        AddStringChoice(ScrapeMetadataField.Description, T("Common.Description", "Description"), _targetItem.Description, result.Description, settings.ImportDescription, StringComparison.Ordinal);
        AddDateChoice(T("Common.ReleaseDate", "Release date"), _targetItem.ReleaseDate, result.ReleaseDate, settings.ImportReleaseDate);
        AddRatingChoice(T("Common.Rating", "Rating"), _targetItem.Rating, result.Rating, settings.ImportRating);
        AddStringChoice(ScrapeMetadataField.Developer, T("Common.Developer", "Developer"), _targetItem.Developer, result.Developer, settings.ImportDeveloper);
        AddStringChoice(ScrapeMetadataField.Genre, T("Common.Genre", "Genre"), _targetItem.Genre, result.Genre, settings.ImportGenre);
        AddStringChoice(ScrapeMetadataField.Platform, T("Common.Platform", "Platform"), _targetItem.Platform, result.Platform, settings.ImportPlatform);
        AddStringChoice(ScrapeMetadataField.Publisher, T("Common.Publisher", "Publisher"), _targetItem.Publisher, result.Publisher, settings.ImportPublisher);
        AddStringChoice(ScrapeMetadataField.Series, T("Common.Series", "Series"), _targetItem.Series, result.Series, settings.ImportSeries);
        AddStringChoice(ScrapeMetadataField.ReleaseType, T("Common.ReleaseType", "Release type"), _targetItem.ReleaseType, result.ReleaseType, settings.ImportReleaseType);
        AddStringChoice(ScrapeMetadataField.SortTitle, T("Common.SortTitle", "Sort title"), _targetItem.SortTitle, result.SortTitle, settings.ImportSortTitle);
        AddStringChoice(ScrapeMetadataField.PlayMode, T("Common.PlayMode", "Play mode"), _targetItem.PlayMode, result.PlayMode, settings.ImportPlayMode);
        AddStringChoice(ScrapeMetadataField.MaxPlayers, T("Common.MaxPlayers", "Max players"), _targetItem.MaxPlayers, result.MaxPlayers, settings.ImportMaxPlayers);
        AddStringChoice(ScrapeMetadataField.Source, T("Common.Source", "Source"), _targetItem.Source, result.Source, settings.ImportSource);

        foreach (var pair in result.VisibleCustomFields)
        {
            _targetItem.CustomFields.TryGetValue(pair.Key, out var current);
            AddStringChoice(
                ScrapeMetadataField.CustomField,
                $"{T("Common.CustomFields", "Custom fields")}: {pair.Key}",
                current,
                pair.Value,
                settings.ImportCustomFields,
                StringComparison.Ordinal,
                pair.Key);
        }

        AddArtworkChoice(AssetType.Cover, T("Button.Cover", "Cover"), result.CoverUrl, settings.ImportCover);
        AddArtworkChoice(AssetType.Wallpaper, T("NodeSettings_ArtworkWallpaperLabel", "Wallpaper"), result.WallpaperUrl, settings.ImportWallpaper);
        AddArtworkChoice(AssetType.Screenshot, T("Button.Screenshot", "Screenshot"), result.ScreenshotUrl, settings.ImportScreenshot);
        AddArtworkChoice(AssetType.Logo, T("Button.Logo", "Logo"), result.LogoUrl, settings.ImportLogo);
        AddArtworkChoice(AssetType.Marquee, T("NodeSettings_ArtworkMarqueeLabel", "Marquee"), result.MarqueeUrl, settings.ImportMarquee);
        AddArtworkChoice(AssetType.Bezel, T("Button.Bezel", "Bezel"), result.BezelUrl, settings.ImportBezel);
        AddArtworkChoice(AssetType.ControlPanel, T("Button.ControlPanel", "Control panel"), result.ControlPanelUrl, settings.ImportControlPanel);
        RefreshChoiceCommandStates();
    }

    private void AddStringChoice(
        ScrapeMetadataField field,
        string label,
        string? currentValue,
        string? incomingValue,
        bool enabledByDefault,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase,
        string? customFieldKey = null)
    {
        var incoming = incomingValue?.Trim();
        if (string.IsNullOrWhiteSpace(incoming))
            return;

        var current = currentValue?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && string.Equals(current, incoming, comparison))
            return;

        MetadataChoices.Add(new ScrapeMetadataChoice(
            field,
            label,
            DisplayValue(current),
            incoming,
            ShouldSelectMetadataByDefault(enabledByDefault, !string.IsNullOrWhiteSpace(current)),
            customFieldKey));
    }

    private void AddDateChoice(
        string label,
        DateTime? currentValue,
        DateTime? incomingValue,
        bool enabledByDefault)
    {
        if (!incomingValue.HasValue)
            return;

        var incoming = incomingValue.Value.Date;
        var current = currentValue?.Date;
        if (current == incoming)
            return;

        MetadataChoices.Add(new ScrapeMetadataChoice(
            ScrapeMetadataField.ReleaseDate,
            label,
            current?.ToString("yyyy-MM-dd") ?? DisplayValue(null),
            incoming.ToString("yyyy-MM-dd"),
            ShouldSelectMetadataByDefault(enabledByDefault, current.HasValue)));
    }

    private void AddRatingChoice(
        string label,
        double currentValue,
        double? incomingValue,
        bool enabledByDefault)
    {
        if (!incomingValue.HasValue)
            return;

        var incoming = Math.Clamp(incomingValue.Value, 0d, 100d);
        var hasCurrent = currentValue > 0d;
        if (hasCurrent && Math.Abs(currentValue - incoming) < 0.0001d)
            return;

        MetadataChoices.Add(new ScrapeMetadataChoice(
            ScrapeMetadataField.Rating,
            label,
            hasCurrent ? currentValue.ToString("0.##") : DisplayValue(null),
            incoming.ToString("0.##"),
            ShouldSelectMetadataByDefault(enabledByDefault, hasCurrent)));
    }

    private bool ShouldSelectMetadataByDefault(bool enabledByDefault, bool hasExistingValue)
    {
        if (!enabledByDefault)
            return false;

        return !hasExistingValue ||
               ImportSettings.ExistingDataMode == ScraperExistingDataMode.OverwriteAlways;
    }

    private void AddArtworkChoice(AssetType type, string label, string? url, bool enabledByDefault)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var hasExisting = _targetItem.Assets.Any(asset => asset.Type == type);
        var status = hasExisting
            ? T("ScrapeDialog.ArtworkExistingStatus", "Existing artwork is retained; this image will be added.")
            : T("ScrapeDialog.ArtworkMissingStatus", "No artwork of this type exists yet.");

        ArtworkChoices.Add(new ScrapeArtworkChoice(
            type,
            label,
            url,
            hasExisting,
            status,
            enabledByDefault && (!hasExisting || ImportSettings.AppendAssetsDuringBulkScrape)));
    }

    private static string DisplayValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void SetMetadataSelection(bool selected)
    {
        foreach (var choice in MetadataChoices)
            choice.IsSelected = selected;
    }

    private void SetArtworkSelection(bool selected)
    {
        foreach (var choice in ArtworkChoices)
            choice.IsSelected = selected;
    }

    private void RefreshChoiceCommandStates()
    {
        SelectAllMetadataCommand.NotifyCanExecuteChanged();
        ClearMetadataSelectionCommand.NotifyCanExecuteChanged();
        SelectAllArtworkCommand.NotifyCanExecuteChanged();
        ClearArtworkSelectionCommand.NotifyCanExecuteChanged();
    }

    private ScraperSearchResult BuildSelectedResult(ScraperSearchResult source)
    {
        var selectedFields = MetadataChoices
            .Where(choice => choice.IsSelected)
            .ToList();

        bool IsSelected(ScrapeMetadataField field)
            => selectedFields.Any(choice => choice.Field == field);

        var selectedArtwork = ArtworkChoices
            .Where(choice => choice.IsSelected)
            .Select(choice => choice.Type)
            .ToHashSet();

        var customFields = selectedFields
            .Where(choice => choice.Field == ScrapeMetadataField.CustomField &&
                             !string.IsNullOrWhiteSpace(choice.CustomFieldKey))
            .ToDictionary(
                choice => choice.CustomFieldKey!,
                choice => source.CustomFields[choice.CustomFieldKey!],
                StringComparer.Ordinal);

        return new ScraperSearchResult
        {
            Id = source.Id,
            Title = source.Title,
            Description = IsSelected(ScrapeMetadataField.Description) ? source.Description : string.Empty,
            ReleaseDate = IsSelected(ScrapeMetadataField.ReleaseDate) ? source.ReleaseDate : null,
            Rating = IsSelected(ScrapeMetadataField.Rating) ? source.Rating : null,
            Developer = IsSelected(ScrapeMetadataField.Developer) ? source.Developer : null,
            Genre = IsSelected(ScrapeMetadataField.Genre) ? source.Genre : null,
            Platform = IsSelected(ScrapeMetadataField.Platform) ? source.Platform : null,
            Publisher = IsSelected(ScrapeMetadataField.Publisher) ? source.Publisher : null,
            Series = IsSelected(ScrapeMetadataField.Series) ? source.Series : null,
            ReleaseType = IsSelected(ScrapeMetadataField.ReleaseType) ? source.ReleaseType : null,
            SortTitle = IsSelected(ScrapeMetadataField.SortTitle) ? source.SortTitle : null,
            PlayMode = IsSelected(ScrapeMetadataField.PlayMode) ? source.PlayMode : null,
            MaxPlayers = IsSelected(ScrapeMetadataField.MaxPlayers) ? source.MaxPlayers : null,
            Source = IsSelected(ScrapeMetadataField.Source) ? source.Source : string.Empty,
            CustomFields = customFields,
            CoverUrl = selectedArtwork.Contains(AssetType.Cover) ? source.CoverUrl : null,
            WallpaperUrl = selectedArtwork.Contains(AssetType.Wallpaper) ? source.WallpaperUrl : null,
            ScreenshotUrl = selectedArtwork.Contains(AssetType.Screenshot) ? source.ScreenshotUrl : null,
            LogoUrl = selectedArtwork.Contains(AssetType.Logo) ? source.LogoUrl : null,
            MarqueeUrl = selectedArtwork.Contains(AssetType.Marquee) ? source.MarqueeUrl : null,
            BezelUrl = selectedArtwork.Contains(AssetType.Bezel) ? source.BezelUrl : null,
            ControlPanelUrl = selectedArtwork.Contains(AssetType.ControlPanel) ? source.ControlPanelUrl : null
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelPreviewEnrichment();

        if (_searchCts != null)
        {
            _searchCts.Cancel();
            if (!IsBusy)
            {
                _searchCts.Dispose();
                _searchCts = null;
            }
        }
    }
}
