using System;

namespace Retromind.Helpers;

internal static class MediaGridLayoutHelper
{
    public static (int ColumnCount, double EffectiveItemWidth) Calculate(
        double viewportWidth,
        double viewportPadding,
        double itemWidth,
        double itemSpacing)
    {
        var availableWidth = viewportWidth - viewportPadding;
        var columnCount = CalculateColumnCount(availableWidth, itemWidth, itemSpacing);
        var effectiveItemWidth = CalculateEffectiveItemWidth(
            availableWidth,
            itemWidth,
            itemSpacing,
            columnCount);

        return (columnCount, effectiveItemWidth);
    }

    private static int CalculateColumnCount(double availableWidth, double itemWidth, double itemSpacing)
    {
        if (availableWidth <= 0 || itemWidth <= 0)
            return 1;

        var totalItemWidth = itemWidth + itemSpacing;
        if (totalItemWidth <= 0)
            return 1;

        return Math.Max(1, (int)Math.Floor((availableWidth + itemSpacing) / totalItemWidth));
    }

    private static double CalculateEffectiveItemWidth(
        double availableWidth,
        double itemWidth,
        double itemSpacing,
        int columnCount)
    {
        if (availableWidth <= 0)
            return itemWidth;

        var totalSpacing = itemSpacing * (columnCount - 1);
        var width = (availableWidth - totalSpacing) / columnCount;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return itemWidth;

        return width;
    }
}
