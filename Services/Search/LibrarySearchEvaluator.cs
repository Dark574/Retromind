using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Search;

internal sealed class LibrarySearchQuery
{
    internal SearchQueryMatcher Matcher { get; }
    internal int? Year { get; }
    internal bool OnlyFavorites { get; }
    internal PlayStatus? Status { get; }
    internal bool HasCriteria { get; }

    internal LibrarySearchQuery(string? text = null, string? year = null, bool onlyFavorites = false, PlayStatus? status = null)
    {
        Matcher = SearchQueryMatcher.Create(text);
        Year = int.TryParse(year?.Trim(), out var parsed) ? parsed : null;
        OnlyFavorites = onlyFavorites;
        Status = status;
        // Preserve the desktop distinction between a nonempty raw query and an active parsed matcher.
        HasCriteria = !string.IsNullOrWhiteSpace(text) || Year.HasValue || onlyFavorites || status.HasValue;
    }
}

/// <summary>No UI, shared mutable state, persistent index or cancellation ownership.</summary>
internal static class LibrarySearchEvaluator
{
    internal static Task<IReadOnlyList<LibrarySearchHit>> EvaluateAsync(LibrarySearchSnapshot snapshot,
        LibrarySearchQuery query, CancellationToken token = default, Action<LibrarySearchHit>? onMatch = null) =>
        Task.Run(() => Evaluate(snapshot, query, token, onMatch), token);

    internal static IReadOnlyList<LibrarySearchHit> Evaluate(LibrarySearchSnapshot snapshot,
        LibrarySearchQuery query, CancellationToken token = default, Action<LibrarySearchHit>? onMatch = null)
    {
        token.ThrowIfCancellationRequested();
        snapshot.VerifyQuery(query);
        if (!query.HasCriteria) return Array.Empty<LibrarySearchHit>();
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = snapshot.Culture;
        try
        {
            var results = new List<LibrarySearchHit>(256);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in snapshot.Entries)
            {
                token.ThrowIfCancellationRequested();
                var data = entry.Document;
                if (query.OnlyFavorites && !data.IsFavorite || query.Status.HasValue && data.Status != query.Status.Value ||
                    query.Year.HasValue && data.ReleaseDate?.Year != query.Year.Value || !query.Matcher.Matches(data)) continue;
                var hit = new LibrarySearchHit(data, entry.Item, entry.Owner, entry.OwnerId, entry.NodeIndex);
                // The caller may collect presentation-specific information from immutable values only.
                // Invoke before ID deduplication to retain the desktop's existing node-count semantics.
                onMatch?.Invoke(hit);
                if (string.IsNullOrWhiteSpace(data.Id) || seen.Add(data.Id)) results.Add(hit);
            }
            token.ThrowIfCancellationRequested();
            try
            {
                results.Sort((left, right) =>
                {
                    token.ThrowIfCancellationRequested();
                    var primary = MediaSortHelper.NaturalStringComparer.Compare(left.Document.PrimarySortKey, right.Document.PrimarySortKey);
                    if (primary != 0) return primary;
                    var title = MediaSortHelper.NaturalStringComparer.Compare(left.Document.TitleSortKey, right.Document.TitleSortKey);
                    return title != 0 ? title : string.Compare(left.ItemId, right.ItemId, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException)
            {
                throw new OperationCanceledException(token);
            }
            token.ThrowIfCancellationRequested();
            return results.AsReadOnly();
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }
}
