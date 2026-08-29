using System;
using Retromind.Models;

namespace Retromind.Helpers;

public static class MediaPlayStateHelper
{
    public static bool HasPlayEvidence(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.PlayCount > 0 || item.TotalPlayTime > TimeSpan.Zero || item.LastPlayed.HasValue;
    }
}
