using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// Builds the non-persisted BigMode home rows from the real library tree.
/// Kept deterministic and UI-independent so ordering/filtering can be tested.
/// </summary>
public static class BigModeHomeBuilder
{
    public const int DefaultItemLimit = 12;

    public static ObservableCollection<BigModeHomeSectionViewModel> Build(
        IEnumerable<MediaNode> roots,
        bool parentalFilterActive,
        string recentlyPlayedTitle,
        string favoritesTitle,
        string libraryTitle,
        string gameCountLabel,
        string backLabel = "Back",
        string openLabel = "Open",
        string overviewLabel = "Overview",
        MediaNode? initialLibraryNode = null,
        int itemLimit = DefaultItemLimit)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemLimit);

        var visibleRoots = roots
            .Where(node => !parentalFilterActive || ShouldShowNode(node))
            .ToArray();
        var items = EnumerateItems(visibleRoots, parentalFilterActive).ToArray();
        var sections = new ObservableCollection<BigModeHomeSectionViewModel>();

        var recent = items
            .Where(entry => entry.Item.LastPlayed.HasValue)
            .OrderByDescending(entry => entry.Item.LastPlayed)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(itemLimit)
            .Select(entry => CreateItemEntry(
                entry.Item,
                entry.SourceNode,
                entry.Item.LastPlayed!.Value.ToString("g", CultureInfo.CurrentCulture)))
            .ToObservableCollection();
        AddNonEmptySection(sections, recentlyPlayedTitle, recent);

        var favorites = items
            .Where(entry => entry.Item.IsFavorite)
            .OrderByDescending(entry => entry.Item.LastPlayed)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(itemLimit)
            .Select(entry => CreateItemEntry(entry.Item, entry.SourceNode, entry.SourceNode.Name))
            .ToObservableCollection();
        AddNonEmptySection(sections, favoritesTitle, favorites);

        if (visibleRoots.Length > 0)
        {
            var libraryNavigator = new BigModeHomeLibraryNavigator(
                visibleRoots,
                parentalFilterActive,
                libraryTitle,
                gameCountLabel,
                backLabel,
                openLabel,
                overviewLabel,
                initialLibraryNode);
            sections.Add(new BigModeHomeSectionViewModel(
                libraryTitle,
                libraryNavigator.BuildEntries(),
                libraryNavigator));
        }

        return sections;
    }

    private static BigModeHomeEntryViewModel CreateItemEntry(
        MediaItem item,
        MediaNode sourceNode,
        string? subtitle) =>
        new()
        {
            Title = item.Title,
            Subtitle = subtitle,
            CoverPath = AssetResolver.ResolveAssetPath(item, sourceNode, AssetType.Cover),
            Item = item,
            SourceNode = sourceNode
        };

    private static IEnumerable<(MediaItem Item, MediaNode SourceNode)> EnumerateItems(
        IEnumerable<MediaNode> nodes,
        bool parentalFilterActive)
    {
        foreach (var node in nodes)
        {
            foreach (var item in node.Items)
            {
                if (!parentalFilterActive || !item.IsProtected)
                    yield return (item, node);
            }

            foreach (var entry in EnumerateItems(node.Children, parentalFilterActive))
                yield return entry;
        }
    }

    private static bool ShouldShowNode(MediaNode node)
    {
        if (node.Children.Count == 0 && node.Items.Count == 0)
            return true;

        if (node.Items.Any(item => !item.IsProtected))
            return true;

        return node.Children.Any(ShouldShowNode);
    }

    private static ObservableCollection<T> ToObservableCollection<T>(this IEnumerable<T> source) =>
        new(source);

    private static void AddNonEmptySection(
        ICollection<BigModeHomeSectionViewModel> sections,
        string title,
        ObservableCollection<BigModeHomeEntryViewModel> entries)
    {
        if (entries.Count > 0)
            sections.Add(new BigModeHomeSectionViewModel(title, entries));
    }
}
