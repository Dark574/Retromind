using Retromind.Models;

namespace Retromind.ViewModels;

public enum BigModeHomeEntryKind
{
    MediaItem,
    LibraryBack,
    LibraryCurrent,
    LibraryChild
}

/// <summary>
/// A non-persisted shortcut shown on the optional BigMode home screen.
/// The original source node remains attached so inherited artwork and launch
/// configuration keep their normal library semantics.
/// </summary>
public sealed class BigModeHomeEntryViewModel
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? CoverPath { get; init; }
    public MediaItem? Item { get; init; }
    public MediaNode? Node { get; init; }
    public MediaNode? SourceNode { get; init; }
    public BigModeHomeEntryKind Kind { get; init; } = BigModeHomeEntryKind.MediaItem;
    public bool HasChildren { get; init; }
    public string? NavigationBadge { get; init; }

    public bool IsMediaItem => Kind == BigModeHomeEntryKind.MediaItem && Item != null;
    public bool IsNode => Node != null;
    public bool IsLibraryBack => Kind == BigModeHomeEntryKind.LibraryBack;
    public bool IsLibraryCurrent => Kind == BigModeHomeEntryKind.LibraryCurrent;
    public bool IsLibraryChild => Kind == BigModeHomeEntryKind.LibraryChild;
    public bool HasNavigationBadge => !string.IsNullOrWhiteSpace(NavigationBadge);
}
