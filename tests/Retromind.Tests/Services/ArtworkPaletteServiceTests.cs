using System.Threading;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class ArtworkPaletteServiceTests
{
    [Fact]
    public void ExtractPaletteFromBgra_UsesVividDominantColor()
    {
        var pixels = CreateSolidPixels(width: 8, height: 8, red: 210, green: 34, blue: 28);

        var palette = ArtworkPaletteService.ExtractPaletteFromBgra(
            pixels,
            width: 8,
            height: 8,
            stride: 8 * 4,
            CancellationToken.None);

        Assert.NotNull(palette);
        Assert.True(palette.Value.Accent.R > palette.Value.Accent.G);
        Assert.True(palette.Value.Accent.R > palette.Value.Accent.B);
        Assert.NotEqual(palette.Value.Accent, palette.Value.SecondaryAccent);
    }

    [Fact]
    public void ExtractPaletteFromBgra_IgnoresNeutralDarkArtwork()
    {
        var pixels = CreateSolidPixels(width: 8, height: 8, red: 8, green: 8, blue: 8);

        var palette = ArtworkPaletteService.ExtractPaletteFromBgra(
            pixels,
            width: 8,
            height: 8,
            stride: 8 * 4,
            CancellationToken.None);

        Assert.Null(palette);
    }

    [Fact]
    public void ExtractPaletteFromBgra_PreservesDistinctSecondaryColor()
    {
        const int width = 8;
        const int height = 8;
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                var useBlue = x >= 6;
                pixels[offset] = useBlue ? (byte)220 : (byte)24;
                pixels[offset + 1] = 30;
                pixels[offset + 2] = useBlue ? (byte)30 : (byte)220;
                pixels[offset + 3] = 255;
            }
        }

        var palette = ArtworkPaletteService.ExtractPaletteFromBgra(
            pixels,
            width,
            height,
            width * 4,
            CancellationToken.None);

        Assert.NotNull(palette);
        Assert.True(palette.Value.Accent.R > palette.Value.Accent.B);
        Assert.True(palette.Value.SecondaryAccent.B > palette.Value.SecondaryAccent.R);
    }

    private static byte[] CreateSolidPixels(
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }
}
