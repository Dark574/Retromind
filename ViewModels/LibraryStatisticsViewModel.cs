using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public sealed class LibraryStatisticsViewModel : ViewModelBase
{
    private const int RankingLimit = 10;

    public string Title => T("Statistics.Title", "Library statistics");
    public string SummaryTitle => T("Statistics.Summary", "Overview");
    public string TotalItemsLabel => T("Statistics.TotalItems", "Items");
    public string TotalPlayTimeLabel => T("Statistics.TotalPlayTime", "Total play time");
    public string TotalLaunchesLabel => T("Statistics.TotalLaunches", "Launches");
    public string FavoritesLabel => T("Statistics.Favorites", "Favorites");
    public string CompletedLabel => T("Statistics.Completed", "Completed");
    public string InProgressLabel => T("Statistics.InProgress", "In progress");
    public string AbandonedLabel => T("Statistics.Abandoned", "Abandoned");
    public string NeverStartedLabel => T("Statistics.NeverStarted", "Never started");
    public string MostPlayedTitle => T("Statistics.MostPlayed", "Most played");
    public string RecentlyPlayedTitle => T("Statistics.RecentlyPlayed", "Recently played");
    public string CategoriesTitle => T("Statistics.ByCategory", "Items by category");
    public string ReleaseYearsTitle => T("Statistics.ByReleaseYear", "Most common release years");
    public string NoPlayDataText => T("Statistics.NoPlayData", "No play data available yet.");
    public string NoReleaseYearDataText => T("Statistics.NoReleaseYearData", "No release year data available.");
    public string CloseButtonText => T("Button_Close", "Close");

    public int TotalItems { get; }
    public TimeSpan TotalPlayTime { get; }
    public int TotalLaunches { get; }
    public int FavoriteItems { get; }
    public int CompletedItems { get; }
    public int InProgressItems { get; }
    public int AbandonedItems { get; }
    public int NeverStartedItems { get; }

    public IReadOnlyList<LibraryStatisticsRankingItem> MostPlayedItems { get; }
    public IReadOnlyList<LibraryStatisticsRankingItem> RecentlyPlayedItems { get; }
    public IReadOnlyList<LibraryStatisticsDistributionItem> CategoryDistribution { get; }
    public IReadOnlyList<LibraryStatisticsDistributionItem> ReleaseYearDistribution { get; }

    public bool HasMostPlayedItems => MostPlayedItems.Count > 0;
    public bool HasRecentlyPlayedItems => RecentlyPlayedItems.Count > 0;
    public bool HasCategoryData => CategoryDistribution.Count > 0;
    public bool HasReleaseYearData => ReleaseYearDistribution.Count > 0;

    public IRelayCommand<Window?> CloseCommand { get; }

    public LibraryStatisticsViewModel(IEnumerable<MediaNode> roots, bool excludeProtectedItems)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var entries = EnumerateItems(roots, [], excludeProtectedItems).ToArray();
        TotalItems = entries.Length;
        TotalPlayTime = TimeSpan.FromTicks(entries.Sum(entry => entry.Item.TotalPlayTime.Ticks));
        TotalLaunches = entries.Sum(entry => Math.Max(0, entry.Item.PlayCount));
        FavoriteItems = entries.Count(entry => entry.Item.IsFavorite);
        CompletedItems = entries.Count(entry => entry.Item.Status == PlayStatus.Completed);
        AbandonedItems = entries.Count(entry => entry.Item.Status == PlayStatus.Abandoned);
        InProgressItems = entries.Count(entry =>
            entry.Item.Status == PlayStatus.Incomplete && HasPlayEvidence(entry.Item));
        NeverStartedItems = entries.Count(entry =>
            entry.Item.Status == PlayStatus.Incomplete && !HasPlayEvidence(entry.Item));

        MostPlayedItems = entries
            .Where(entry => entry.Item.TotalPlayTime > TimeSpan.Zero)
            .OrderByDescending(entry => entry.Item.TotalPlayTime)
            .ThenByDescending(entry => entry.Item.PlayCount)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(RankingLimit)
            .Select((entry, index) => CreateRankingItem(entry, index + 1))
            .ToArray();

        RecentlyPlayedItems = entries
            .Where(entry => entry.Item.LastPlayed.HasValue)
            .OrderByDescending(entry => entry.Item.LastPlayed)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(RankingLimit)
            .Select((entry, index) => CreateRankingItem(entry, index + 1))
            .ToArray();

        CategoryDistribution = CreateDistribution(
            entries.GroupBy(entry => entry.CategoryPath, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => (Label: group.Key, Count: group.Count())),
            TotalItems,
            RankingLimit);

        ReleaseYearDistribution = CreateDistribution(
            entries.Where(entry => entry.Item.ReleaseDate.HasValue)
                .GroupBy(entry => entry.Item.ReleaseDate!.Value.Year)
                .Select(group => (Label: group.Key.ToString(CultureInfo.CurrentCulture), Count: group.Count())),
            TotalItems,
            RankingLimit);

        CloseCommand = new RelayCommand<Window?>(window => window?.Close());
    }

    private static bool HasPlayEvidence(MediaItem item)
        => item.PlayCount > 0 || item.TotalPlayTime > TimeSpan.Zero || item.LastPlayed.HasValue;

    private static LibraryStatisticsRankingItem CreateRankingItem(
        LibraryStatisticsEntry entry,
        int rank)
    {
        return new LibraryStatisticsRankingItem(
            rank,
            entry.Item.Title,
            entry.CategoryPath,
            FormatPlayTime(entry.Item.TotalPlayTime),
            entry.Item.PlayCount,
            entry.Item.LastPlayed?.ToString("g", CultureInfo.CurrentCulture) ?? "–");
    }

    private static IReadOnlyList<LibraryStatisticsDistributionItem> CreateDistribution(
        IEnumerable<(string Label, int Count)> source,
        int totalItems,
        int limit)
    {
        return source
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(entry => new LibraryStatisticsDistributionItem(
                entry.Label,
                entry.Count,
                totalItems == 0 ? 0 : entry.Count * 100d / totalItems,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Statistics.DistributionValueFormat", "{0:N0} ({1:N1}%)"),
                    entry.Count,
                    totalItems == 0 ? 0 : entry.Count * 100d / totalItems)))
            .ToArray();
    }

    private static IEnumerable<LibraryStatisticsEntry> EnumerateItems(
        IEnumerable<MediaNode> nodes,
        IReadOnlyList<string> parentPath,
        bool excludeProtectedItems)
    {
        foreach (var node in nodes)
        {
            var path = parentPath.Concat([node.Name]).ToArray();
            var categoryPath = string.Join(" / ", path);

            foreach (var item in node.Items)
            {
                if (!excludeProtectedItems || !item.IsProtected)
                    yield return new LibraryStatisticsEntry(item, categoryPath);
            }

            foreach (var entry in EnumerateItems(node.Children, path, excludeProtectedItems))
                yield return entry;
        }
    }

    private static string FormatPlayTime(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return T("TimePlayed.Never", "Never played");

        if (value.TotalMinutes < 1)
            return "< 1m";

        if (value.TotalHours < 1)
            return $"{(int)value.TotalMinutes}m";

        if (value.TotalDays < 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";

        return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
    }

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    private sealed record LibraryStatisticsEntry(MediaItem Item, string CategoryPath);
}

public sealed record LibraryStatisticsRankingItem(
    int Rank,
    string Title,
    string Category,
    string PlayTime,
    int PlayCount,
    string LastPlayed);

public sealed record LibraryStatisticsDistributionItem(
    string Label,
    int Count,
    double Percentage,
    string DisplayValue);
