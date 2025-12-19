namespace Retromind.Models;

public enum MediaFileKind
{
    Absolute = 0,
    // Future-proof: allows portable "mount IDs" later without changing the item schema again.
    MountRelative = 1,
    // Optional for later if you ever support copying ROMs into the library.
    LibraryRelative = 2,
}

public sealed class MediaFileRef
{
    public MediaFileKind Kind { get; set; } = MediaFileKind.Absolute;

    /// <summary>
    /// Absolute path (Kind=Absolute) or a relative path (Kind=MountRelative/LibraryRelative).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Optional display label (e.g. "Disc 1", "Side B").
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Optional order index (1..n). Used for default launch selection.
    /// </summary>
    public int? Index { get; set; }
}