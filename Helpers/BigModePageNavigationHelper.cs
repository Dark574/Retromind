using System;

namespace Retromind.Helpers;

internal static class BigModePageNavigationHelper
{
    public static int GetTargetIndex(int currentIndex, int itemCount, int pageSize, int direction)
    {
        if (itemCount <= 0 || direction == 0)
            return -1;

        var normalizedPageSize = Math.Max(1, pageSize);
        if (currentIndex < 0 || currentIndex >= itemCount)
            return direction < 0 ? itemCount - 1 : 0;

        if (direction < 0)
            return currentIndex == 0 ? itemCount - 1 : Math.Max(0, currentIndex - normalizedPageSize);

        return currentIndex == itemCount - 1
            ? 0
            : Math.Min(itemCount - 1, currentIndex + normalizedPageSize);
    }
}
