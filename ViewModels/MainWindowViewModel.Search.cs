using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<string> SavedSearchTerms { get; } = new();

    // Remembers where the user came from before entering global search.
    private string? _searchReturnNodeId;
    private string? _searchReturnItemId;
    private string? _pendingGlobalSearchSelectionItemId;
    private bool _restoreSearchUiStateOnNextContentBuild;
    private readonly SearchUiState _searchUiState = new();

    private sealed class SearchUiState
    {
        public string SharedSearchText { get; set; } = string.Empty;
        public bool SharedOnlyFavorites { get; set; }
        public HashSet<string> GlobalScopeNodeIds { get; } = new(StringComparer.Ordinal);
        public bool HasGlobalScopeSelection { get; set; }
    }

    private void SyncSavedSearchTermsCollectionFromSettings()
    {
        _currentSettings.SavedSearchTerms ??= new List<string>();
        var normalizedTerms = _currentSettings.SavedSearchTerms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _currentSettings.SavedSearchTerms = normalizedTerms;

        SavedSearchTerms.Clear();
        foreach (var savedTerm in normalizedTerms)
            SavedSearchTerms.Add(savedTerm);
    }

    private void MutateSavedSearchTerms(Func<List<string>, bool> mutate)
    {
        _currentSettings.SavedSearchTerms ??= new List<string>();
        var terms = _currentSettings.SavedSearchTerms;

        if (!mutate(terms))
            return;

        SyncSavedSearchTermsCollectionFromSettings();
        SaveSettingsOnly();
    }

    private static string NormalizeSavedSearchTerm(string term) => term.Trim();

    public void SaveSearchTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return;

        var normalizedTerm = NormalizeSavedSearchTerm(term);

        MutateSavedSearchTerms(terms =>
        {
            var existingIndex = terms.FindIndex(t =>
                string.Equals(t, normalizedTerm, StringComparison.OrdinalIgnoreCase));

            if (existingIndex == 0 &&
                string.Equals(terms[0], normalizedTerm, StringComparison.Ordinal))
            {
                return false;
            }

            if (existingIndex >= 0)
                terms.RemoveAt(existingIndex);

            terms.Insert(0, normalizedTerm);
            return true;
        });
    }

    public void RemoveSavedSearchTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return;

        var normalizedTerm = NormalizeSavedSearchTerm(term);
        MutateSavedSearchTerms(terms =>
            terms.RemoveAll(t => string.Equals(t, normalizedTerm, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    private void DetachSearchAreaHandlers()
    {
        if (_currentSearchAreaVm == null)
            return;

        _searchUiState.SharedSearchText = _currentSearchAreaVm.SearchText ?? string.Empty;
        _searchUiState.SharedOnlyFavorites = _currentSearchAreaVm.OnlyFavorites;
        _searchUiState.GlobalScopeNodeIds.Clear();
        foreach (var id in _currentSearchAreaVm.GetSelectedScopeIdsSnapshot())
            _searchUiState.GlobalScopeNodeIds.Add(id);
        _searchUiState.HasGlobalScopeSelection = true;

        _currentSearchAreaVm.RequestPlay -= OnSearchAreaRequestPlay;
        _currentSearchAreaVm.PropertyChanged -= OnSearchAreaPropertyChanged;
        _currentSearchAreaVm.SearchResults.CollectionChanged -= OnSearchAreaResultsChanged;
        _currentSearchAreaVm.Dispose();
        _currentSearchAreaVm = null;
    }

    private void AttachSearchAreaHandlers(SearchAreaViewModel searchVm)
    {
        _currentSearchAreaVm = searchVm;
        searchVm.RequestPlay += OnSearchAreaRequestPlay;
        searchVm.PropertyChanged += OnSearchAreaPropertyChanged;
        searchVm.SearchResults.CollectionChanged += OnSearchAreaResultsChanged;
    }

    private void OnSearchAreaRequestPlay(MediaItem item)
    {
        _ = PlayMediaAsync(item);
    }

    private void OnSearchAreaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SearchAreaViewModel searchVm)
            return;

        if (e.PropertyName == nameof(SearchAreaViewModel.ItemWidth))
        {
            ItemWidth = searchVm.ItemWidth;
            SaveSettingsOnly();
            return;
        }

        if (e.PropertyName == nameof(SearchAreaViewModel.SearchText))
        {
            _searchUiState.SharedSearchText = searchVm.SearchText ?? string.Empty;
            return;
        }

        if (e.PropertyName == nameof(SearchAreaViewModel.OnlyFavorites))
        {
            _searchUiState.SharedOnlyFavorites = searchVm.OnlyFavorites;
            return;
        }

        if (e.PropertyName == nameof(SearchAreaViewModel.SelectedScopeCount))
        {
            _searchUiState.GlobalScopeNodeIds.Clear();
            foreach (var id in searchVm.GetSelectedScopeIdsSnapshot())
                _searchUiState.GlobalScopeNodeIds.Add(id);
            _searchUiState.HasGlobalScopeSelection = true;
            return;
        }

        if (e.PropertyName != nameof(SearchAreaViewModel.SelectedMediaItem))
            return;

        var item = searchVm.SelectedMediaItem;
        if (item != null)
            _pendingGlobalSearchSelectionItemId = null;

        if (item != null && CanCheckGogUpdatesForItem(item))
            _ = CheckGogUpdatesForItemCoreAsync(item, force: true, CancellationToken.None);

        NotifyPlayAvailabilityChanged();

        OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
        OnPropertyChanged(nameof(ResolvedDisplayNode));

        if (!_currentSettings.EnableSelectionMusicPreview)
        {
            _audioService.StopMusic();
            return;
        }

        var musicAsset = item?.GetPrimaryAssetPath(AssetType.Music);
        if (!string.IsNullOrEmpty(musicAsset))
        {
            var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(musicAsset);
            if (!string.IsNullOrWhiteSpace(fullPath))
                _ = _audioService.PlayMusicAsync(fullPath);
            else
                _audioService.StopMusic();
        }
        else
            _audioService.StopMusic();
    }

    private void OnSearchAreaResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        TryApplyPendingGlobalSearchSelection();
        UpdateLibraryGameCounters();
    }

    private void TryApplyPendingGlobalSearchSelection()
    {
        if (string.IsNullOrWhiteSpace(_pendingGlobalSearchSelectionItemId))
            return;

        var searchVm = _currentSearchAreaVm;
        if (searchVm == null || searchVm.SelectedMediaItem != null)
            return;

        var pendingId = _pendingGlobalSearchSelectionItemId;
        var candidate = searchVm.SearchResults.FirstOrDefault(i => i.Id == pendingId);
        if (candidate == null)
            return;

        searchVm.SelectedMediaItem = candidate;
        _pendingGlobalSearchSelectionItemId = null;
    }
}
