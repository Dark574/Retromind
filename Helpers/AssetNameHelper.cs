using System;
using System.IO;
using Retromind.Models;

namespace Retromind.Helpers;

public static class AssetNameHelper
{
    public const string DisplayMarker = "__RM__";

    public static bool RequiresDisplayPrefix(AssetType type)
    {
        return type == AssetType.Manual || type == AssetType.Music;
    }

    public static bool TrySplitDisplayPrefix(string prefix, out string display, out string suffix)
    {
        display = string.Empty;
        suffix = string.Empty;

        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        var markerIndex = prefix.IndexOf(DisplayMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
            return false;

        display = prefix[..markerIndex];
        suffix = prefix[(markerIndex + DisplayMarker.Length)..];

        return !string.IsNullOrWhiteSpace(display) && !string.IsNullOrWhiteSpace(suffix);
    }

    public static string BuildDisplayPrefix(string display, string suffix)
    {
        return $"{display}{DisplayMarker}{suffix}";
    }

    public static string ExtractDisplayPrefixOrFallback(string prefix)
    {
        if (TrySplitDisplayPrefix(prefix, out var display, out _))
            return display;

        return prefix;
    }

    public static string GetDisplayLabel(AssetType type, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        if (!RequiresDisplayPrefix(type))
            return relativePath;

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        if (TrySplitDisplayPrefix(fileName, out var display, out _))
            return display;

        return fileName;
    }
}
