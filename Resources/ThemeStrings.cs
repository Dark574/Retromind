namespace Retromind.Resources;

/// <summary>
/// Exposes localized text to themes that are loaded from XAML at runtime.
/// </summary>
public static class ThemeStrings
{
    private static string Get(string key, string fallback) =>
        Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    public static string GameCountLabel =>
        Get("BigMode_GameCountLabel", "Games");

    public static string Preview => Get("Common.PreviewShort", "Preview");
    public static string NoPreviewVideo => Get("Theme.NoPreviewVideo", "No preview video");
    public static string GameLabel => Get("Theme.GameLabel", "Game:");
    public static string MovieLabel => Get("Theme.MovieLabel", "Movie:");
    public static string NoSystemVideo => Get("Theme.NoSystemVideo", "No system video");
    public static string SystemPreviewVideo => Get("Theme.SystemPreviewVideo", "System preview video");
    public static string ArchiveIndex => Get("Theme.Archive.Index", "INDEX");
    public static string ArchiveItem => Get("Theme.Archive.Item", "ITEM");
    public static string ArchiveOf => Get("Theme.Archive.Of", "OF");
    public static string ArchiveYear => Get("Theme.Archive.Year", "YEAR");
    public static string ArchiveBy => Get("Theme.Archive.By", "BY");
    public static string ArchiveDetails => Get("Theme.Archive.Details", "DETAILS");
    public static string ArchiveGenre => Get("Theme.Archive.Genre", "GENRE");
    public static string ArchivePlayers => Get("Theme.Archive.Players", "PLAYERS");
    public static string ArchiveRating => Get("Theme.Archive.Rating", "RATING");
    public static string ArchiveStatus => Get("Theme.Archive.Status", "STATUS");
    public static string ArchivePlayCount => Get("Theme.Archive.PlayCount", "PLAY COUNT");
    public static string ArchiveLastPlayed => Get("Theme.Archive.LastPlayed", "LAST PLAYED");
    public static string ArchiveTotalPlayTime => Get("Theme.Archive.TotalPlayTime", "TOTAL PLAY TIME");
}
