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

        _currentSettings.SavedSearchOnlyFavorites ??=
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var normalizedFavoriteStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var savedTerm in normalizedTerms)
        {
            if (TryGetSavedSearchFavoriteState(savedTerm, out var onlyFavorites) && onlyFavorites)
                normalizedFavoriteStates[savedTerm] = true;
        }

        _currentSettings.SavedSearchOnlyFavorites = normalizedFavoriteStates;

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

        var onlyFavorites = GetCurrentOnlyFavorites();
        var termsChanged = false;
        _currentSettings.SavedSearchTerms ??= new List<string>();
        var terms = _currentSettings.SavedSearchTerms;
        var existingIndex = terms.FindIndex(t =>
            string.Equals(t, normalizedTerm, StringComparison.OrdinalIgnoreCase));

        if (existingIndex != 0 ||
            !string.Equals(terms[0], normalizedTerm, StringComparison.Ordinal))
        {
            if (existingIndex >= 0)
                terms.RemoveAt(existingIndex);

            terms.Insert(0, normalizedTerm);
            termsChanged = true;
        }

        _currentSettings.SavedSearchOnlyFavorites ??=
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var stateChanged = SetSavedSearchFavoriteState(normalizedTerm, onlyFavorites);
        if (!termsChanged && !stateChanged)
            return;

        SyncSavedSearchTermsCollectionFromSettings();
        SaveSettingsOnly();
    }

    public void RemoveSavedSearchTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return;

        var normalizedTerm = NormalizeSavedSearchTerm(term);
        var removedState = RemoveSavedSearchFavoriteState(normalizedTerm);
        MutateSavedSearchTerms(terms =>
            terms.RemoveAll(t => string.Equals(t, normalizedTerm, StringComparison.OrdinalIgnoreCase)) > 0 || removedState);
    }

    private bool GetCurrentOnlyFavorites()
        => _currentSearchAreaVm?.OnlyFavorites
           ?? _currentMediaAreaVm?.OnlyFavorites
           ?? _searchUiState.SharedOnlyFavorites;

    private bool TryGetSavedSearchFavoriteState(string term, out bool onlyFavorites)
    {
        onlyFavorites = false;
        var states = _currentSettings.SavedSearchOnlyFavorites;
        if (states == null)
            return false;

        foreach (var pair in states)
        {
            if (!string.Equals(pair.Key, term, StringComparison.OrdinalIgnoreCase))
                continue;

            onlyFavorites = pair.Value;
            return true;
        }

        return false;
    }

    private bool SetSavedSearchFavoriteState(string term, bool onlyFavorites)
    {
        var states = _currentSettings.SavedSearchOnlyFavorites;
        var existingKey = states.Keys.FirstOrDefault(key =>
            string.Equals(key, term, StringComparison.OrdinalIgnoreCase));

        if (!onlyFavorites)
        {
            return existingKey != null && states.Remove(existingKey);
        }

        if (existingKey != null && states[existingKey])
            return false;

        if (existingKey != null)
            states.Remove(existingKey);

        states[term] = true;
        return true;
    }

    private bool RemoveSavedSearchFavoriteState(string term)
    {
        var states = _currentSettings.SavedSearchOnlyFavorites;
        if (states == null)
            return false;

        var key = states.Keys.FirstOrDefault(existing =>
            string.Equals(existing, term, StringComparison.OrdinalIgnoreCase));
        return key != null && states.Remove(key);
    }

    private void ApplySavedSearchFavoriteState(string? term, Action<bool> apply)
    {
        if (string.IsNullOrWhiteSpace(term))
            return;

        var normalizedTerm = NormalizeSavedSearchTerm(term);
        if (!SavedSearchTerms.Any(saved =>
                string.Equals(saved, normalizedTerm, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        TryGetSavedSearchFavoriteState(normalizedTerm, out var onlyFavorites);
        apply(onlyFavorites);
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
            ApplySavedSearchFavoriteState(searchVm.SearchText, value => searchVm.OnlyFavorites = value);
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
            _ = CheckGogUpdatesForItemCoreAsync(item, force: false, CancellationToken.None);

        NotifyPlayAvailabilityChanged();

        OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
        OnPropertyChanged(nameof(ResolvedDisplayNode));

        var contextNode = item == null ? null : FindParentNode(RootItems, item);
        _ = PlaySelectionMusicAsync(item, contextNode);
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
