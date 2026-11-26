using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>One matcher over live UI models or immutable search values, without cloning live models.</summary>
internal readonly struct SearchQueryData
{
    private readonly MediaItem? _live;
    private readonly MediaSearchDocument? _snapshot;
    internal SearchQueryData(MediaItem live) { _live = live; _snapshot = null; }
    internal SearchQueryData(MediaSearchDocument snapshot) { _snapshot = snapshot; _live = null; }
    internal string Id => _snapshot is { } s ? s.Id : _live!.Id;
    internal string Title => _snapshot is { } s ? s.Title : _live!.Title;
    internal string? SortTitle => _snapshot is { } s ? s.SortTitle : _live!.SortTitle;
    internal string? Description => _snapshot is { } s ? s.Description : _live!.Description;
    internal string? Developer => _snapshot is { } s ? s.Developer : _live!.Developer;
    internal string? Publisher => _snapshot is { } s ? s.Publisher : _live!.Publisher;
    internal string? Platform => _snapshot is { } s ? s.Platform : _live!.Platform;
    internal string? Source => _snapshot is { } s ? s.Source : _live!.Source;
    internal string? Genre => _snapshot is { } s ? s.Genre : _live!.Genre;
    internal string? Series => _snapshot is { } s ? s.Series : _live!.Series;
    internal string? ReleaseType => _snapshot is { } s ? s.ReleaseType : _live!.ReleaseType;
    internal string? PlayMode => _snapshot is { } s ? s.PlayMode : _live!.PlayMode;
    internal string? MaxPlayers => _snapshot is { } s ? s.MaxPlayers : _live!.MaxPlayers;
    internal DateTime? ReleaseDate => _snapshot is { } s ? s.ReleaseDate : _live!.ReleaseDate;
    internal double Rating => _snapshot is { } s ? s.Rating : _live!.Rating;
    internal PlayStatus Status => _snapshot is { } s ? s.Status : _live!.Status;
    internal bool IsFavorite => _snapshot is { } s ? s.IsFavorite : _live!.IsFavorite;
    internal bool HasPlayed => _snapshot is { } s ? s.HasPlayed : MediaPlayStateHelper.HasPlayEvidence(_live!);
    internal string? StoreProviderId => _snapshot is { } s ? s.StoreProviderId : StoreProviderBadgeHelper.GetProviderId(_live);
    internal (bool IsGog, bool HasUpdate) GogUpdateSearchState => _snapshot is { } s ? s.GogUpdateSearchState : _live!.GogUpdateSearchState;
    internal int TagCount => _snapshot is { } s ? s.TagCount : _live!.Tags.Count;
    internal string TagAt(int index) => _snapshot is { } s ? s.TagAt(index) : _live!.Tags[index];
    internal IReadOnlyDictionary<string, string> CustomFields => _snapshot is { } s ? s.CustomFields : _live!.CustomFields;
    internal int AssetCount => _snapshot is { } s ? s.AssetCount : _live!.Assets.Count;
    internal AssetType AssetTypeAt(int index) => _snapshot is { } s ? s.AssetAt(index).Type : _live!.Assets[index].Type;
    internal string AssetPathAt(int index) => _snapshot is { } s ? s.AssetAt(index).RelativePath : _live!.Assets[index].RelativePath;
}
