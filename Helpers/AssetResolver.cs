using Retromind.Extensions;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Central helper to resolve the most appropriate artwork path
/// for a given media item and its node context.
/// Resolution order:
/// 1) Item-level asset (absolute path via MediaItem helpers)
/// 2) Node-level asset (relative path resolved via AppPaths)
/// 3) Theme-level fallback (relative to the active theme directory)
/// </summary>
public static class AssetResolver
{
    /// <summary>
    /// Resolves an absolute path for the requested asset type using the standard fallback order.
    /// </summary>
    /// <param name="item">The media item (game, movie, etc.).</param>
    /// <param name="node">
    /// Optional node that hosts the item. Can provide node-level artwork for platforms/categories.
    /// </param>
    /// <param name="type">Type of the artwork to resolve (Bezel, ControlPanel, Marquee, etc.).</param>
    /// <param name="themeFallbackRelativePath">
    /// Optional path inside the current theme directory used as final fallback,
    /// e.g. "Images/cabinet_bezel.png".
    /// </param>
    /// <returns>
    /// Absolute file system path if any source provides a value; otherwise null.
    /// </returns>
    public static string? ResolveAssetPath(
        MediaItem item,
        MediaNode? node,
        AssetType type,
        string? themeFallbackRelativePath = null)
    {
        if (item is null)
            return null;

        // 1) Item-level asset (MediaItem already returns absolute path for primary assets).
        var itemPath = GetItemPrimaryAssetPath(item, type);
        if (!string.IsNullOrWhiteSpace(itemPath))
            return itemPath;

        // 2) Node-level asset (relative path; resolve via AppPaths).
        if (node is not null)
        {
            var nodeRel = node.GetPrimaryAssetPath(type);
            if (!string.IsNullOrWhiteSpace(nodeRel))
            {
                return AppPaths.ResolveDataPath(nodeRel!);
            }
        }

        // 3) Theme-level fallback (relative to active theme base directory).
        if (!string.IsNullOrWhiteSpace(themeFallbackRelativePath))
        {
            return ThemeProperties.GetThemeFilePath(themeFallbackRelativePath);
        }

        return null;
    }

    /// <summary>
    /// Helper that maps an AssetType to the corresponding MediaItem primary asset property.
    /// Returns an absolute path if available.
    /// </summary>
    private static string? GetItemPrimaryAssetPath(MediaItem item, AssetType type)
    {
        return type switch
        {
            AssetType.Cover        => item.PrimaryCoverPath,
            AssetType.Wallpaper    => item.PrimaryWallpaperPath,
            AssetType.Logo         => item.PrimaryLogoPath,
            AssetType.Video        => item.PrimaryVideoPath,
            AssetType.Marquee      => item.PrimaryMarqueePath,
            AssetType.Banner       => item.PrimaryBannerPath,
            AssetType.Bezel        => item.PrimaryBezelPath,
            AssetType.ControlPanel => item.PrimaryControlPanelPath,
            _                      => null
        };
    }
}