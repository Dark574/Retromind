using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public sealed partial class LibraryStatisticsViewModel : ViewModelBase
{
    private const int RankingItemLimit = 10;
    private const int DistributionItemLimit = 10;

    private readonly IReadOnlyList<MediaNode> _roots;
    private readonly bool _excludeProtectedItems;

    public string Title => T("Statistics.Title", "Library statistics");
    public string SummaryTitle => T("Statistics.Summary", "Overview");
    public string ScopeLabel => T("Statistics.Scope", "Scope:");
    public string DistributionLabel => T("Statistics.Distribution", "Distribution:");
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
    public string NoPlayDataText => T("Statistics.NoPlayData", "No play data available yet.");
    public string NoDistributionDataText => T("Statistics.NoDistributionData", "No matching metadata available.");
    public string OpenItemToolTip => T("Statistics.OpenItem", "Show item in library");
    public string FilterCardToolTip => T("Statistics.FilterCard", "Show matching items");
    public string CloseButtonText => T("Button_Close", "Close");

    public IReadOnlyList<LibraryStatisticsScopeOption> ScopeOptions { get; }
    public IReadOnlyList<LibraryStatisticsDistributionOption> DistributionOptions { get; }

    [ObservableProperty]
    private LibraryStatisticsScopeOption _selectedScope;

    [ObservableProperty]
    private LibraryStatisticsDistributionOption _selectedDistribution;

    public int TotalItems { get; private set; }
    public TimeSpan TotalPlayTime { get; private set; }
    public int TotalLaunches { get; private set; }
    public int FavoriteItems { get; private set; }
    public int CompletedItems { get; private set; }
    public int InProgressItems { get; private set; }
    public int AbandonedItems { get; private set; }
    public int NeverStartedItems { get; private set; }

    public IReadOnlyList<LibraryStatisticsRankingItem> MostPlayedItems { get; private set; } = [];
    public IReadOnlyList<LibraryStatisticsRankingItem> RecentlyPlayedItems { get; private set; } = [];
    public IReadOnlyList<LibraryStatisticsDistributionItem> DistributionItems { get; private set; } = [];

    public bool HasMostPlayedItems => MostPlayedItems.Count > 0;
    public bool HasRecentlyPlayedItems => RecentlyPlayedItems.Count > 0;
    public bool HasDistributionData => DistributionItems.Count > 0;

    public MediaItem? NavigationTarget { get; private set; }
    public LibraryStatisticsFilterRequest? FilterRequest { get; private set; }

    public IRelayCommand<Window?> CloseCommand { get; }
    public IRelayCommand<LibraryStatisticsRankingItem?> OpenItemCommand { get; }
    public IRelayCommand<LibraryStatisticsFilterKind> ApplyFilterCommand { get; }

    public event Action? RequestClose;

    public LibraryStatisticsViewModel(
        IEnumerable<MediaNode> roots,
        bool excludeProtectedItems)
    {
        ArgumentNullException.ThrowIfNull(roots);

        _roots = roots.ToArray();
        _excludeProtectedItems = excludeProtectedItems;

        var scopes = new List<LibraryStatisticsScopeOption>
        {
            new(T("Statistics.Scope.All", "Entire library"), null)
        };
        scopes.AddRange(CreateScopeOptions(_roots, [], excludeProtectedItems));

        ScopeOptions = scopes;
        DistributionOptions =
        [
            new(T("Statistics.Distribution.Category", "Category"), LibraryStatisticsDistributionKind.Category),
            new(T("Statistics.Distribution.Platform", "Platform"), LibraryStatisticsDistributionKind.Platform),
            new(T("Statistics.Distribution.Genre", "Genre"), LibraryStatisticsDistributionKind.Genre),
            new(T("Statistics.Distribution.Year", "Release year"), LibraryStatisticsDistributionKind.ReleaseYear),
            new(T("Statistics.Distribution.Status", "Status"), LibraryStatisticsDistributionKind.Status)
        ];

        _selectedScope = ScopeOptions[0];
        _selectedDistribution = DistributionOptions[0];
        CloseCommand = new RelayCommand<Window?>(window => window?.Close());
        OpenItemCommand = new RelayCommand<LibraryStatisticsRankingItem?>(OpenItem);
        ApplyFilterCommand = new RelayCommand<LibraryStatisticsFilterKind>(ApplyFilter);

        RefreshStatistics();
    }

    partial void OnSelectedScopeChanged(LibraryStatisticsScopeOption value)
        => RefreshStatistics();

    partial void OnSelectedDistributionChanged(LibraryStatisticsDistributionOption value)
        => RefreshStatistics();

    private void RefreshStatistics()
    {
        var sourceNodes = SelectedScope.Node == null
            ? _roots
            : new[] { SelectedScope.Node };
        var entries = EnumerateItems(sourceNodes, [], _excludeProtectedItems).ToArray();

        TotalItems = entries.Length;
        TotalPlayTime = TimeSpan.FromTicks(entries.Sum(entry => entry.Item.TotalPlayTime.Ticks));
        TotalLaunches = entries.Sum(entry => Math.Max(0, entry.Item.PlayCount));
        FavoriteItems = entries.Count(entry => entry.Item.IsFavorite);
        CompletedItems = entries.Count(entry => entry.Item.Status == PlayStatus.Completed);
        AbandonedItems = entries.Count(entry => entry.Item.Status == PlayStatus.Abandoned);
        InProgressItems = entries.Count(entry =>
            entry.Item.Status == PlayStatus.Incomplete && MediaPlayStateHelper.HasPlayEvidence(entry.Item));
        NeverStartedItems = entries.Count(entry =>
            entry.Item.Status == PlayStatus.Incomplete && !MediaPlayStateHelper.HasPlayEvidence(entry.Item));

        MostPlayedItems = entries
            .Where(entry => entry.Item.TotalPlayTime > TimeSpan.Zero)
            .OrderByDescending(entry => entry.Item.TotalPlayTime)
            .ThenByDescending(entry => entry.Item.PlayCount)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(RankingItemLimit)
            .Select((entry, index) => CreateRankingItem(entry, index + 1))
            .ToArray();

        RecentlyPlayedItems = entries
            .Where(entry => entry.Item.LastPlayed.HasValue)
            .OrderByDescending(entry => entry.Item.LastPlayed)
            .ThenBy(entry => entry.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(RankingItemLimit)
            .Select((entry, index) => CreateRankingItem(entry, index + 1))
            .ToArray();

        DistributionItems = CreateDistribution(entries, SelectedDistribution.Kind);

        OnPropertyChanged(nameof(TotalItems));
        OnPropertyChanged(nameof(TotalPlayTime));
        OnPropertyChanged(nameof(TotalLaunches));
        OnPropertyChanged(nameof(FavoriteItems));
        OnPropertyChanged(nameof(CompletedItems));
        OnPropertyChanged(nameof(InProgressItems));
        OnPropertyChanged(nameof(AbandonedItems));
        OnPropertyChanged(nameof(NeverStartedItems));
        OnPropertyChanged(nameof(MostPlayedItems));
        OnPropertyChanged(nameof(RecentlyPlayedItems));
        OnPropertyChanged(nameof(DistributionItems));
        OnPropertyChanged(nameof(HasMostPlayedItems));
        OnPropertyChanged(nameof(HasRecentlyPlayedItems));
        OnPropertyChanged(nameof(HasDistributionData));
    }

    private void OpenItem(LibraryStatisticsRankingItem? rankingItem)
    {
        if (rankingItem == null)
            return;

        NavigationTarget = rankingItem.Item;
        RequestClose?.Invoke();
    }

    private void ApplyFilter(LibraryStatisticsFilterKind kind)
    {
        FilterRequest = new LibraryStatisticsFilterRequest(SelectedScope.Node, kind);
        RequestClose?.Invoke();
    }

    private static LibraryStatisticsRankingItem CreateRankingItem(
        LibraryStatisticsEntry entry,
        int rank)
    {
        return new LibraryStatisticsRankingItem(
            entry.Item,
            rank,
            entry.Item.Title,
            entry.CategoryPath,
            FormatPlayTime(entry.Item.TotalPlayTime),
            entry.Item.LastPlayed?.ToString("g", CultureInfo.CurrentCulture) ?? "–");
    }

    private IReadOnlyList<LibraryStatisticsDistributionItem> CreateDistribution(
        IReadOnlyList<LibraryStatisticsEntry> entries,
        LibraryStatisticsDistributionKind kind)
    {
        var unspecified = T("Statistics.Distribution.Unspecified", "Not specified");
        IEnumerable<(string Label, int Count)> source = kind switch
        {
            LibraryStatisticsDistributionKind.Category => GroupByLabel(
                entries,
                entry => entry.CategoryPath,
                unspecified),
            LibraryStatisticsDistributionKind.Platform => GroupByLabel(
                entries,
                entry => entry.Item.Platform,
                unspecified),
            LibraryStatisticsDistributionKind.Genre => GroupByLabel(
                entries,
                entry => entry.Item.Genre,
                unspecified),
            LibraryStatisticsDistributionKind.ReleaseYear => GroupByLabel(
                entries,
                entry => entry.Item.ReleaseDate?.Year.ToString(CultureInfo.CurrentCulture),
                unspecified),
            LibraryStatisticsDistributionKind.Status => entries
                .GroupBy(entry => GetStatusLabel(entry.Item.Status), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => (group.Key, group.Count())),
            _ => []
        };

        return source
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(DistributionItemLimit)
            .Select(entry => new LibraryStatisticsDistributionItem(
                entry.Label,
                entries.Count == 0 ? 0 : entry.Count * 100d / entries.Count,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Statistics.DistributionValueFormat", "{0:N0} ({1:N1}%)"),
                    entry.Count,
                    entries.Count == 0 ? 0 : entry.Count * 100d / entries.Count)))
            .ToArray();
    }

    private static IEnumerable<(string Label, int Count)> GroupByLabel(
        IEnumerable<LibraryStatisticsEntry> entries,
        Func<LibraryStatisticsEntry, string?> selector,
        string unspecified)
    {
        return entries
            .GroupBy(
                entry => NormalizeDistributionLabel(selector(entry), unspecified),
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group => (group.Key, group.Count()));
    }

    private static IEnumerable<LibraryStatisticsScopeOption> CreateScopeOptions(
        IEnumerable<MediaNode> nodes,
        IReadOnlyList<string> parentPath,
        bool excludeHiddenNodes)
    {
        foreach (var node in nodes)
        {
            if (excludeHiddenNodes && !node.IsVisibleInTree)
                continue;

            var path = parentPath.Concat([node.Name]).ToArray();
            yield return new LibraryStatisticsScopeOption(string.Join(" / ", path), node);

            foreach (var option in CreateScopeOptions(node.Children, path, excludeHiddenNodes))
                yield return option;
        }
    }

    private static string NormalizeDistributionLabel(string? value, string unspecified)
        => string.IsNullOrWhiteSpace(value) ? unspecified : value.Trim();

    private static string GetStatusLabel(PlayStatus status)
    {
        return status switch
        {
            PlayStatus.Completed => T("Statistics.Completed", "Completed"),
            PlayStatus.Abandoned => T("Statistics.Abandoned", "Abandoned"),
            _ => T("Statistics.Status.Incomplete", "Incomplete")
        };
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

public sealed record LibraryStatisticsScopeOption(string Label, MediaNode? Node);

public sealed record LibraryStatisticsDistributionOption(
    string Label,
    LibraryStatisticsDistributionKind Kind);

public enum LibraryStatisticsDistributionKind
{
    Category,
    Platform,
    Genre,
    ReleaseYear,
    Status
}

public enum LibraryStatisticsFilterKind
{
    Favorites,
    Completed,
    InProgress,
    Abandoned,
    NeverStarted
}

public sealed record LibraryStatisticsFilterRequest(
    MediaNode? ScopeNode,
    LibraryStatisticsFilterKind Kind);

public sealed record LibraryStatisticsRankingItem(
    MediaItem Item,
    int Rank,
    string Title,
    string Category,
    string PlayTime,
    string LastPlayed);

public sealed record LibraryStatisticsDistributionItem(
    string Label,
    double Percentage,
    string DisplayValue);
