using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Retromind.Helpers;

namespace Retromind.Models;

/// <summary>
/// Immutable row view into request-owned, read-only search columns. Unneeded columns are absent.
/// No live model or filesystem access. Values are not serialized or retained between requests.
/// </summary>
internal readonly struct MediaSearchDocument(MediaSearchValues values, int index)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyCustomFields =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    internal string Id => values.Id is { } column ? column[index] : string.Empty;
    internal string Title => values.Title is { } column ? column[index] : string.Empty;
    internal string? SortTitle => values.SortTitle is { } column ? column[index] : null;
    internal string? Description => values.Description is { } column ? column[index] : null;
    internal string? Developer => values.Developer is { } column ? column[index] : null;
    internal string? Publisher => values.Publisher is { } column ? column[index] : null;
    internal string? Platform => values.Platform is { } column ? column[index] : null;
    internal string? Source => values.Source is { } column ? column[index] : null;
    internal string? Genre => values.Genre is { } column ? column[index] : null;
    internal string? Series => values.Series is { } column ? column[index] : null;
    internal string? ReleaseType => values.ReleaseType is { } column ? column[index] : null;
    internal string? PlayMode => values.PlayMode is { } column ? column[index] : null;
    internal string? MaxPlayers => values.MaxPlayers is { } column ? column[index] : null;
    internal DateTime? ReleaseDate => values.ReleaseDate is { } column ? column[index] : null;
    internal double Rating => values.Rating is { } column ? column[index] : 0;
    internal PlayStatus Status => values.Status is { } column ? column[index] : default;
    internal MediaType MediaType => values.MediaType is { } column ? column[index] : default;
    internal bool IsFavorite => values.IsFavorite is { } column ? column[index] : false;
    internal bool HasPlayed => values.HasPlayed is { } column ? column[index] : false;
    internal string? StoreProviderId => StoreProviderBadgeHelper.GetProviderId(this);
    internal string? StoredStoreProviderId => values.StoredStoreProviderId is { } column ? column[index] : null;
    internal string? StoreLauncherArgs => values.StoreLauncherArgs is { } column ? column[index] : null;
    internal string? StoreDataRoot => values.StoreDataRoot;
    internal string? StoreDataRootPrefix => values.StoreDataRootPrefix;
    internal int StoreFileCount => values.StoreFileOffsets is { } offsets ? offsets[index + 1] - offsets[index] : 0;
    internal MediaSearchFile StoreFileAt(int offset) => values.StoreFiles![values.StoreFileOffsets![index] + offset];
    internal (bool IsGog, bool HasUpdate) GogUpdateSearchState => values.GogUpdateSearchState is { } column ? column[index] : default;
    internal int TagCount => values.TagOffsets is { } offsets ? offsets[index + 1] - offsets[index] : 0;
    internal string TagAt(int offset) => values.TagValues![values.TagOffsets![index] + offset];
    internal IReadOnlyDictionary<string, string> CustomFields => values.CustomFields is { } column ? column[index] : EmptyCustomFields;
    internal int AssetCount => values.AssetOffsets is { } offsets ? offsets[index + 1] - offsets[index] : 0;
    internal MediaSearchAsset AssetAt(int offset) => values.AssetValues![values.AssetOffsets![index] + offset];
    internal string PrimarySortKey => values.PrimarySortKey is { } column ? column[index] : string.Empty;
    internal string TitleSortKey => values.TitleSortKey is { } column ? column[index] : string.Empty;
}

/// <summary>All populated columns and nested collections are frozen before publication.</summary>
internal sealed record MediaSearchValues
{
    internal IReadOnlyList<string>? Id { get; init; }
    internal IReadOnlyList<string>? Title { get; init; }
    internal IReadOnlyList<string?>? SortTitle { get; init; }
    internal IReadOnlyList<string?>? Description { get; init; }
    internal IReadOnlyList<string?>? Developer { get; init; }
    internal IReadOnlyList<string?>? Publisher { get; init; }
    internal IReadOnlyList<string?>? Platform { get; init; }
    internal IReadOnlyList<string?>? Source { get; init; }
    internal IReadOnlyList<string?>? Genre { get; init; }
    internal IReadOnlyList<string?>? Series { get; init; }
    internal IReadOnlyList<string?>? ReleaseType { get; init; }
    internal IReadOnlyList<string?>? PlayMode { get; init; }
    internal IReadOnlyList<string?>? MaxPlayers { get; init; }
    internal IReadOnlyList<DateTime?>? ReleaseDate { get; init; }
    internal IReadOnlyList<double>? Rating { get; init; }
    internal IReadOnlyList<PlayStatus>? Status { get; init; }
    internal IReadOnlyList<MediaType>? MediaType { get; init; }
    internal IReadOnlyList<bool>? IsFavorite { get; init; }
    internal IReadOnlyList<bool>? HasPlayed { get; init; }
    internal IReadOnlyList<string?>? StoredStoreProviderId { get; init; }
    internal IReadOnlyList<string?>? StoreLauncherArgs { get; init; }
    internal string? StoreDataRoot { get; init; }
    internal string? StoreDataRootPrefix { get; init; }
    internal IReadOnlyList<int>? StoreFileOffsets { get; init; }
    internal IReadOnlyList<MediaSearchFile>? StoreFiles { get; init; }
    internal IReadOnlyList<(bool IsGog, bool HasUpdate)>? GogUpdateSearchState { get; init; }
    internal IReadOnlyList<int>? TagOffsets { get; init; }
    internal IReadOnlyList<string>? TagValues { get; init; }
    internal IReadOnlyList<IReadOnlyDictionary<string, string>>? CustomFields { get; init; }
    internal IReadOnlyList<int>? AssetOffsets { get; init; }
    internal IReadOnlyList<MediaSearchAsset>? AssetValues { get; init; }
    internal IReadOnlyList<string>? PrimarySortKey { get; init; }
    internal IReadOnlyList<string>? TitleSortKey { get; init; }
}

internal readonly record struct MediaSearchAsset(AssetType Type, string RelativePath);
internal readonly record struct MediaSearchFile(MediaFileKind Kind, string Path, int? Index);
