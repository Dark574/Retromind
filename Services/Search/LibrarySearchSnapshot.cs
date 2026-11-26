using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Search;

internal sealed record LibrarySearchNode(string Id, int ParentIndex);

/// <summary>Live references are opaque handles during evaluation; only Document is read by workers.</summary>
internal readonly record struct LibrarySearchEntry(MediaSearchDocument Document, MediaItem Item, MediaNode Owner, string OwnerId, int NodeIndex);
internal readonly record struct LibrarySearchHit(MediaSearchDocument Document, MediaItem Item, MediaNode Owner, string OwnerId, int NodeIndex)
{
    internal string ItemId => Document.Id;
    internal MediaType MediaType => Document.MediaType;
}

internal sealed class LibrarySearchSnapshot
{
    private static readonly IReadOnlyDictionary<string, string> EmptyCustomFields =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal IReadOnlyList<LibrarySearchEntry> Entries { get; }
    internal IReadOnlyList<LibrarySearchNode> Nodes { get; }
    internal CultureInfo Culture { get; }
    private readonly LibrarySearchQuery? _projectionQuery;

    private LibrarySearchSnapshot(List<LibrarySearchEntry> entries, List<LibrarySearchNode> nodes, LibrarySearchQuery? projectionQuery)
    {
        Entries = entries.AsReadOnly();
        Nodes = nodes.AsReadOnly();
        Culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
        _projectionQuery = projectionQuery;
    }

    internal void VerifyQuery(LibrarySearchQuery query)
    {
        if (_projectionQuery != null && !ReferenceEquals(_projectionQuery, query))
            throw new InvalidOperationException("A query-specific snapshot can only be evaluated with its captured query.");
    }

    /// <summary>
    /// Call on the model-owning/UI thread. Null scope means all roots; an empty set means none.
    /// A supplied node visibility policy excludes its whole subtree. Desktop retains its existing
    /// item-protection rule; other consumers can supply their own effective node policy.
    /// </summary>
    internal static LibrarySearchSnapshot Capture(IEnumerable<MediaNode> roots, IReadOnlySet<string>? scopeNodeIds = null,
        bool parentalFilterActive = false, Func<MediaNode, bool>? isNodeVisible = null, CancellationToken token = default,
        LibrarySearchQuery? query = null)
    {
        token.ThrowIfCancellationRequested();
        var entries = new List<LibrarySearchEntry>();
        var sourceNodes = new List<MediaNode>();
        var itemCapacity = 0;
        var nodes = new List<LibrarySearchNode>();
        if (scopeNodeIds is { Count: 0 }) return new(entries, nodes, query);
        var stack = new Stack<(MediaNode Node, int ParentIndex)>();
        foreach (var root in roots.Reverse()) stack.Push((root, -1));
        var visited = new HashSet<MediaNode>();
        while (stack.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            var node = current.Node;
            if (!visited.Add(node) || (isNodeVisible != null && !isNodeVisible(node))) continue;
            var included = current.ParentIndex >= 0 || scopeNodeIds == null || scopeNodeIds.Contains(node.Id);
            var index = -1;
            if (included)
            {
                index = nodes.Count;
                nodes.Add(new(node.Id, current.ParentIndex));
                sourceNodes.Add(node);
                itemCapacity += node.Items.Count;
            }
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], index));
        }
        entries = new List<LibrarySearchEntry>(itemCapacity);
        for (var nodeIndex = 0; nodeIndex < sourceNodes.Count; nodeIndex++)
        {
            var node = sourceNodes[nodeIndex];
            foreach (var item in node.Items)
            {
                token.ThrowIfCancellationRequested();
                if (!parentalFilterActive || !item.IsProtected)
                    entries.Add(new(default, item, node, node.Id, nodeIndex));
            }
        }
        var values = CaptureValues(entries, query, token);
        for (var i = 0; i < entries.Count; i++) entries[i] = entries[i] with { Document = new(values, i) };
        return new(entries, nodes, query);
    }

    private static MediaSearchValues CaptureValues(List<LibrarySearchEntry> entries, LibrarySearchQuery? query, CancellationToken token)
    {
        var matcher = query?.Matcher;
        bool Needs(string property) => matcher == null || matcher.NeedsSearchField(property);
        IReadOnlyList<T>? Column<T>(bool needed, Func<MediaItem, T> select)
        {
            if (!needed) return null;
            var values = new T[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                values[i] = select(entries[i].Item);
            }
            return Array.AsReadOnly(values);
        }
        // Flatten nested collections so a large library does not allocate one array/wrapper per item.
        (IReadOnlyList<int>? Offsets, IReadOnlyList<T>? Values) Nested<T>(bool needed,
            Func<MediaItem, int> count, Func<MediaItem, int, T> value)
        {
            if (!needed) return (null, null);
            var offsets = new int[entries.Count + 1];
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                offsets[i + 1] = checked(offsets[i] + count(entries[i].Item));
            }
            var values = new T[offsets[^1]];
            for (var i = 0; i < entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                for (var j = offsets[i]; j < offsets[i + 1]; j++) values[j] = value(entries[i].Item, j - offsets[i]);
            }
            return (Array.AsReadOnly(offsets), Array.AsReadOnly(values));
        }
        var tags = Nested(matcher == null || matcher.NeedsSearchTags, item => item.Tags.Count, (item, i) => item.Tags[i]);
        var assets = Nested(matcher == null || matcher.NeedsSearchAssets, item => item.Assets.Count,
            (item, i) => new MediaSearchAsset(item.Assets[i].Type, item.Assets[i].RelativePath));
        var needsStore = matcher == null || matcher.NeedsSearchStore;
        var files = Nested(needsStore, item => item.Files?.Count ?? 0, (item, i) =>
        {
            token.ThrowIfCancellationRequested();
            var file = item.Files[i];
            var path = file.Path;
            // Windows drive/root-relative paths depend on process context. Freeze that rare
            // case here; ordinary portable relative paths are resolved by the worker.
            if (file.Kind == MediaFileKind.LibraryRelative && Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            {
                try { path = Path.GetFullPath(path); }
                catch { path = string.Empty; }
            }
            return new MediaSearchFile(file.Kind, path, file.Index);
        });
        string? storeRoot = null;
        if (needsStore)
        {
            try { storeRoot = Path.GetFullPath(AppPaths.DataRoot); }
            catch { /* Match the existing resolver's invalid-root behavior for portable paths. */ }
        }
        return new MediaSearchValues
        {
            TagOffsets = tags.Offsets, TagValues = tags.Values,
            AssetOffsets = assets.Offsets, AssetValues = assets.Values,
            Id = Column(true, item => item.Id),
            Title = Column(true, item => item.Title),
            SortTitle = Column(Needs(nameof(MediaItem.SortTitle)), item => item.SortTitle),
            Description = Column(Needs(nameof(MediaItem.Description)), item => item.Description),
            Developer = Column(Needs(nameof(MediaItem.Developer)), item => item.Developer),
            Publisher = Column(Needs(nameof(MediaItem.Publisher)), item => item.Publisher),
            Platform = Column(Needs(nameof(MediaItem.Platform)), item => item.Platform),
            Source = Column(Needs(nameof(MediaItem.Source)), item => item.Source),
            Genre = Column(Needs(nameof(MediaItem.Genre)), item => item.Genre),
            Series = Column(Needs(nameof(MediaItem.Series)), item => item.Series),
            ReleaseType = Column(Needs(nameof(MediaItem.ReleaseType)), item => item.ReleaseType),
            PlayMode = Column(Needs(nameof(MediaItem.PlayMode)), item => item.PlayMode),
            MaxPlayers = Column(Needs(nameof(MediaItem.MaxPlayers)), item => item.MaxPlayers),
            ReleaseDate = Column(Needs(nameof(MediaItem.ReleaseDate)) || query!.Year.HasValue, item => item.ReleaseDate),
            Rating = Column(Needs(nameof(MediaItem.Rating)), item => item.Rating),
            Status = Column(Needs(nameof(MediaItem.Status)) || query!.Status.HasValue, item => item.Status),
            MediaType = Column(true, item => item.MediaType),
            IsFavorite = Column(Needs(nameof(MediaItem.IsFavorite)) || query!.OnlyFavorites, item => item.IsFavorite),
            HasPlayed = Column(Needs(nameof(MediaItem.HasPlayedForSearch)), item => MediaPlayStateHelper.HasPlayEvidence(item)),
            StoredStoreProviderId = Column(needsStore, item => item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreProviderId, out var provider) ? provider : null),
            StoreLauncherArgs = Column(needsStore, item => item.LauncherArgs),
            StoreFileOffsets = files.Offsets, StoreFiles = files.Values,
            StoreDataRoot = storeRoot,
            StoreDataRootPrefix = storeRoot == null || storeRoot.EndsWith(Path.DirectorySeparatorChar) ? storeRoot : storeRoot + Path.DirectorySeparatorChar,
            GogUpdateSearchState = Column(matcher == null || matcher.NeedsSearchGogUpdate, item => item.GogUpdateSearchState),
            CustomFields = Column(matcher == null || matcher.NeedsSearchCustomFields, item => item.CustomFields.Count == 0 ? EmptyCustomFields : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(item.CustomFields))),
            PrimarySortKey = Column(true, item => MediaSortHelper.GetPrimarySortKey(item)),
            TitleSortKey = Column(true, item => MediaSortHelper.GetTitleSortKey(item.Title)),
        };
    }
}
