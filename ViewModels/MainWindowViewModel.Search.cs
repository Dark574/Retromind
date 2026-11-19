using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<string> SavedSearchTerms { get; } = new();

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

    private bool GetCurrentOnlyFavorites() => _desktopContent.OnlyFavorites;

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

    private void OpenIntegratedSearch()
    {
        if (_desktopContent.SearchArea != null)
        {
            CloseIntegratedSearch();
            return;
        }

        _audioService.StopMusic();
        _desktopContent.OpenSearch(SelectedNode?.Id, () =>
        {
            _selectedNode = null;
            OnPropertyChanged(nameof(SelectedNode));
            return new SearchAreaViewModel(RootItems, _errorService, IsParentalFilterActive) { ItemWidth = ItemWidth };
        });
    }

    private void CloseIntegratedSearch()
    {
        var (returnNodeId, selectedSearchItemId, desiredItemId) = _desktopContent.CloseSearch();
        MediaNode? targetNode = null;

        if (!string.IsNullOrWhiteSpace(selectedSearchItemId))
            TryFindNodeByMediaId(RootItems, selectedSearchItemId, out targetNode);

        if (targetNode == null && !string.IsNullOrWhiteSpace(returnNodeId))
            targetNode = FindNodeById(RootItems, returnNodeId);

        if (targetNode == null && !string.IsNullOrWhiteSpace(desiredItemId))
            TryFindNodeByMediaId(RootItems, desiredItemId, out targetNode);

        if (targetNode != null && !targetNode.IsVisibleInTree)
            targetNode = null;

        targetNode ??= FindFirstVisibleNode();
        if (targetNode == null)
        {
            _selectedNode = null;
            OnPropertyChanged(nameof(SelectedNode));
            UpdateContent();
            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            return;
        }

        if (!string.IsNullOrWhiteSpace(desiredItemId))
            _desktopContent.RememberNodeSelection(targetNode.Id, desiredItemId);

        ExpandPathToNode(RootItems, targetNode);
        SelectedNode = targetNode;
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
            ApplySavedSearchFavoriteState(searchVm.SearchText, value => searchVm.OnlyFavorites = value);
            return;
        }

        if (e.PropertyName == nameof(SearchAreaViewModel.IsMultiSelectMode))
        {
            OnPropertyChanged(nameof(AreSingleItemActionsEnabled));
            NotifyPlayAvailabilityChanged();
            return;
        }

        if (e.PropertyName != nameof(SearchAreaViewModel.SelectedMediaItem))
            return;

        var item = searchVm.SelectedMediaItem;

        if (item != null && CanCheckGogUpdatesForItem(item))
            _ = CheckGogUpdatesForItemCoreAsync(item, force: false, CancellationToken.None);

        NotifyPlayAvailabilityChanged();

        OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
        OnPropertyChanged(nameof(ResolvedDisplayNode));

        _ = RetroAchievementsProgress.SelectItemAsync(item);
        var contextNode = item == null ? null : FindParentNode(RootItems, item);
        _ = PlaySelectionMusicAsync(item, contextNode);
    }
}
