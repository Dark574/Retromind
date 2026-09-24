using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Retromind.Services;

internal readonly record struct ArtworkPalette(Color Accent, Color SecondaryAccent);

/// <summary>
/// Extracts a small, vivid two-color palette from local artwork. Results are
/// cached by path and file stamp because theme selection commonly revisits the
/// same covers while navigating back and forth.
/// </summary>
internal sealed class ArtworkPaletteService
{
    private const int DecodeWidth = 64;
    private const int MaxCacheEntries = 256;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    public Task<ArtworkPalette?> GetPaletteAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult<ArtworkPalette?>(null);

        FileInfo file;
        try
        {
            file = new FileInfo(path);
        }
        catch
        {
            return Task.FromResult<ArtworkPalette?>(null);
        }

        var stamp = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return Task.FromResult(cached.Palette);
        }

        return Task.Run(() => LoadAndExtract(path, stamp, cancellationToken), cancellationToken);
    }

    private ArtworkPalette? LoadAndExtract(
        string path,
        FileStamp stamp,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = File.OpenRead(path);
            using var bitmap = Bitmap.DecodeToWidth(stream, DecodeWidth);
            cancellationToken.ThrowIfCancellationRequested();

            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            if (width <= 0 || height <= 0)
                return null;

            using var pixels = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
            using var framebuffer = pixels.Lock();
            bitmap.CopyPixels(framebuffer);

            var bufferSize = framebuffer.RowBytes * framebuffer.Size.Height;
            var buffer = new byte[bufferSize];
            Marshal.Copy(framebuffer.Address, buffer, 0, bufferSize);

            var palette = ExtractPaletteFromBgra(
                buffer,
                framebuffer.Size.Width,
                framebuffer.Size.Height,
                framebuffer.RowBytes,
                cancellationToken);

            Store(path, stamp, palette);
            return palette;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Store(path, stamp, null);
            return null;
        }
    }

    internal static ArtworkPalette? ExtractPaletteFromBgra(
        byte[] pixels,
        int width,
        int height,
        int stride,
        CancellationToken cancellationToken = default)
    {
        if (pixels.Length == 0 || width <= 0 || height <= 0 || stride < width * 4)
            return null;

        var buckets = new Dictionary<int, ColorBucket>();

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = y * stride;

            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                var alpha = pixels[offset + 3];
                if (alpha < 96)
                    continue;

                var blue = Unpremultiply(pixels[offset], alpha);
                var green = Unpremultiply(pixels[offset + 1], alpha);
                var red = Unpremultiply(pixels[offset + 2], alpha);

                RgbToHsv(red, green, blue, out _, out var saturation, out var value);
                if (value < 0.08 || value > 0.97 || saturation < 0.12)
                    continue;

                var key = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                if (!buckets.TryGetValue(key, out var bucket))
                    bucket = default;

                bucket.Add(red, green, blue);
                buckets[key] = bucket;
            }
        }

        if (buckets.Count == 0)
            return null;

        Color? primary = null;
        var primaryScore = double.MinValue;
        foreach (var bucket in buckets.Values)
        {
            var color = bucket.Average;
            var score = Score(color, bucket.Count);
            if (score <= primaryScore)
                continue;

            primary = color;
            primaryScore = score;
        }

        if (primary is not { } primaryRaw)
            return null;

        RgbToHsv(primaryRaw.R, primaryRaw.G, primaryRaw.B, out var primaryHue, out _, out _);

        Color? secondary = null;
        var secondaryScore = double.MinValue;
        foreach (var bucket in buckets.Values)
        {
            var color = bucket.Average;
            RgbToHsv(color.R, color.G, color.B, out var hue, out _, out _);
            var hueDistance = HueDistance(primaryHue, hue);
            if (hueDistance < 32)
                continue;

            var score = Score(color, bucket.Count) * (0.75 + (hueDistance / 360));
            if (score <= secondaryScore)
                continue;

            secondary = color;
            secondaryScore = score;
        }

        var accent = NormalizeAccent(primaryRaw);
        var secondaryAccent = secondary is { } secondaryRaw
            ? NormalizeAccent(secondaryRaw)
            : CreateCompanionAccent(accent);

        return new ArtworkPalette(accent, secondaryAccent);
    }

    private void Store(string path, FileStamp stamp, ArtworkPalette? palette)
    {
        lock (_cacheLock)
        {
            if (!_cache.ContainsKey(path))
                _cacheOrder.Enqueue(path);

            _cache[path] = new CacheEntry(stamp, palette);

            while (_cache.Count > MaxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
                _cache.Remove(oldest);
        }
    }

    private static double Score(Color color, int count)
    {
        RgbToHsv(color.R, color.G, color.B, out _, out var saturation, out var value);
        var brightnessWeight = 1 - Math.Min(0.75, Math.Abs(value - 0.62));
        return count * (0.35 + (saturation * 1.65)) * brightnessWeight;
    }

    private static Color NormalizeAccent(Color color)
    {
        RgbToHsv(color.R, color.G, color.B, out var hue, out var saturation, out var value);
        return HsvToColor(
            hue,
            Math.Clamp(saturation, 0.55, 0.90),
            Math.Clamp(value, 0.68, 0.95));
    }

    private static Color CreateCompanionAccent(Color accent)
    {
        RgbToHsv(accent.R, accent.G, accent.B, out var hue, out var saturation, out var value);
        return HsvToColor(
            (hue + 58) % 360,
            Math.Clamp(saturation * 0.9, 0.50, 0.85),
            Math.Clamp(value, 0.72, 0.95));
    }

    private static byte Unpremultiply(byte value, byte alpha)
    {
        if (alpha == 0 || alpha == 255)
            return value;

        return (byte)Math.Clamp((value * 255) / alpha, 0, 255);
    }

    private static double HueDistance(double first, double second)
    {
        var distance = Math.Abs(first - second);
        return Math.Min(distance, 360 - distance);
    }

    private static void RgbToHsv(
        byte red,
        byte green,
        byte blue,
        out double hue,
        out double saturation,
        out double value)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        hue = 0;
        if (delta > 0.0001)
        {
            if (Math.Abs(max - r) < 0.0001)
                hue = 60 * (((g - b) / delta) % 6);
            else if (Math.Abs(max - g) < 0.0001)
                hue = 60 * (((b - r) / delta) + 2);
            else
                hue = 60 * (((r - g) / delta) + 4);

            if (hue < 0)
                hue += 360;
        }

        saturation = max <= 0.0001 ? 0 : delta / max;
        value = max;
    }

    private static Color HsvToColor(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var segment = hue / 60;
        var x = chroma * (1 - Math.Abs((segment % 2) - 1));

        (double r, double g, double b) = segment switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };

        var match = value - chroma;
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((r + match) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + match) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + match) * 255), 0, 255));
    }

    private readonly record struct FileStamp(long Length, long LastWriteTicks);

    private readonly record struct CacheEntry(FileStamp Stamp, ArtworkPalette? Palette);

    private struct ColorBucket
    {
        private long _red;
        private long _green;
        private long _blue;

        public int Count { get; private set; }

        public Color Average => Color.FromRgb(
            (byte)(_red / Count),
            (byte)(_green / Count),
            (byte)(_blue / Count));

        public void Add(byte red, byte green, byte blue)
        {
            _red += red;
            _green += green;
            _blue += blue;
            Count++;
        }
    }
}
