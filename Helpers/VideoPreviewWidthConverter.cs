using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Helpers.Video;

namespace Retromind.Helpers;

/// <summary>
/// Fits a floating preview slot to the active frame's aspect ratio at a bounded
/// height/width. A right-aligned slot then keeps narrower videos at the same edge.
/// Inputs: active surface index, surface A, surface B, first-frame revision.
/// </summary>
public sealed class VideoPreviewWidthConverter : IMultiValueConverter
{
    public double PreviewHeight { get; set; } = 535;
    public double MaxWidth { get; set; } = 950;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3 || values[0] is not int index || index is < 0 or > 1 ||
            values[index + 1] is not IVideoSurface surface)
            return MaxWidth;

        int width, height;
        if (surface is LibVlcVideoSurface vlcSurface)
        {
            if (!vlcSurface.TryGetFrameSize(out width, out height))
                return MaxWidth;
        }
        else
        {
            width = surface.Width;
            height = surface.Height;
        }

        return width > 0 && height > 0
            ? Math.Min(MaxWidth, PreviewHeight * width / height)
            : MaxWidth;
    }
}
