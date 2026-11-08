using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Retromind.Models;
using Retromind.Services;
using SkiaSharp;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to load images asynchronously from URLs or local paths.
/// Features an LRU (Least Recently Used) Cache, Downsampling, and Task Cancellation.
/// </summary>
public partial class AsyncImageHelper : AvaloniaObject
{
    // --- Configuration ---

    private const int MaxCacheSize = 200;
    internal const long MaxCacheBytes = 128L * 1024 * 1024;

    private const int DesktopDecodeConcurrency = 4;
    internal const int MaximumHttpImageBytes = 32 * 1024 * 1024;
    internal const int MaximumImageDimension = 16_384;
    internal const long MaximumImagePixels = 32L * 1024 * 1024;
    private const int DownloadBufferBytes = 64 * 1024;

    // Bounded, per-source/code suppression, independent of bitmap cache/ref-count lifetime.
    private static readonly object FailureReportLock = new();
    private static readonly Dictionary<string, long> LastFailureReports = new(StringComparer.Ordinal);
    private static readonly Queue<string> FailureReportOrder = new();
    private const int MaximumFailureReportKeys = 256;
    private static readonly long FailureReportIntervalMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    // Skia bitmap decoding and resizing is CPU-heavy and cannot be interrupted
    // once it has entered the native codec. BigMode therefore keeps its serial
    // lane for responsive carousel navigation, while the desktop lane allows a
    // small amount of parallelism so its initial cover grid does not fill one
    // image at a time.
    private static readonly SemaphoreSlim DesktopDecodeGate =
        new(DesktopDecodeConcurrency, DesktopDecodeConcurrency);
    private static readonly SemaphoreSlim SerialDecodeGate = new(1, 1);

    // Shared HttpClient to prevent socket exhaustion (used only if DI is not available)
    private static readonly HttpClient FallbackHttpClient = new();

    private static HttpClient GetHttpClient()
    {
        var svc = Retromind.App.Current?.Services;
        if (svc != null && svc.GetService(typeof(HttpClient)) is HttpClient client)
            return client;

        return FallbackHttpClient;
    }

    // --- Cache State ---
    private static readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node, long Bytes)> Cache = new();
    private static readonly Dictionary<string, int> CacheRefCounts = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InvalidatedKeys = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();
    private static long _cachedBitmapBytes;

    internal static (int Count, long Bytes) GetCacheUsage()
    {
        lock (CacheLock) return (Cache.Count, _cachedBitmapBytes);
    }

    private readonly record struct CacheAddResult(
        Bitmap Bitmap,
        bool IsCached,
        CacheLease? AssignmentLease);

    private sealed class CacheLease : IDisposable
    {
        private string? _key;

        public CacheLease(string key, Bitmap bitmap)
        {
            _key = key;
            Bitmap = bitmap;
        }

        public Bitmap Bitmap { get; }

        public void Dispose()
        {
            var key = Interlocked.Exchange(ref _key, null);
            if (key != null)
                DecrementCacheRef(key);
        }
    }
    
    // Transparent 1x1 fallback to avoid null Image.Source crashes during measure.
    private static readonly IImage PlaceholderImage = CreatePlaceholderImage();

    static AsyncImageHelper()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = args.NewValue as string;
            var width = GetDecodeWidth(image);
            var disableCache = GetDisableCache(image);
            var useSerialDecode = GetUseSerialDecode(image);
            _ = LoadImageAsync(image, url, width, disableCache, useSerialDecode);
        });

        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = args.NewValue as int?;
            var disableCache = GetDisableCache(image);
            var useSerialDecode = GetUseSerialDecode(image);
            _ = LoadImageAsync(image, url, width, disableCache, useSerialDecode);
        });

        DisableCacheProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = GetDecodeWidth(image);
            var disableCache = args.NewValue is bool b && b;
            var useSerialDecode = GetUseSerialDecode(image);
            _ = LoadImageAsync(image, url, width, disableCache, useSerialDecode);
        });

        UseSerialDecodeProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = GetDecodeWidth(image);
            var disableCache = GetDisableCache(image);
            var useSerialDecode = args.NewValue is bool b && b;
            _ = LoadImageAsync(image, url, width, disableCache, useSerialDecode);
        });
    }

    // --- Attached Properties ---

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("Url");

    public static string? GetUrl(Image element) => element.GetValue(UrlProperty);
    public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);

    public static readonly AttachedProperty<int?> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, int?>("DecodeWidth");

    public static int? GetDecodeWidth(Image element) => element.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image element, int? value) => element.SetValue(DecodeWidthProperty, value);

    public static readonly AttachedProperty<bool> DisableCacheProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("DisableCache");

    public static bool GetDisableCache(Image element) => element.GetValue(DisableCacheProperty);
    public static void SetDisableCache(Image element, bool value) => element.SetValue(DisableCacheProperty, value);

    public static readonly AttachedProperty<bool> UseSerialDecodeProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, AvaloniaObject, bool>(
            "UseSerialDecode",
            defaultValue: false,
            inherits: true);

    public static bool GetUseSerialDecode(AvaloniaObject element) =>
        element.GetValue(UseSerialDecodeProperty);

    public static void SetUseSerialDecode(AvaloniaObject element, bool value) =>
        element.SetValue(UseSerialDecodeProperty, value);

    public static readonly AttachedProperty<bool> IsLoadedProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("IsLoaded");

    public static bool GetIsLoaded(Image element) => element.GetValue(IsLoadedProperty);
    public static void SetIsLoaded(Image element, bool value) => element.SetValue(IsLoadedProperty, value);

    // Private property to store the Cancellation Token Source for the current load operation on this Image control
    private static readonly AttachedProperty<CancellationTokenSource?> CurrentLoadCtsProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, CancellationTokenSource?>("CurrentLoadCts");

    // Owns either a cache reference or a share of an uncached in-flight result.
    private static readonly AttachedProperty<BitmapLease?> CurrentBitmapLeaseProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, BitmapLease?>("CurrentBitmapLease");

    // Tracks whether we've attached a Detach handler for this Image instance
    private static readonly AttachedProperty<bool> DetachHandlerAttachedProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("DetachHandlerAttached");

    // --- Core Logic ---

    /// <summary>
    /// Resets the image source and cancels any ongoing load operation.
    /// </summary>
    private static void ResetImage(Image image)
    {
        var oldCts = image.GetValue(CurrentLoadCtsProperty);
        oldCts?.Cancel();
        oldCts?.Dispose();
        image.Source = PlaceholderImage;
        ReleaseImageLease(image);
        image.SetValue(IsLoadedProperty, false);
        image.SetValue(CurrentLoadCtsProperty, null);
    }
    
    private static async Task LoadImageAsync(
        Image image,
        string? url,
        int? decodeWidth,
        bool disableCache,
        bool useSerialDecode)
    {
        ResetImage(image);

        if (string.IsNullOrEmpty(url)) return;

        var cts = new CancellationTokenSource();
        image.SetValue(CurrentLoadCtsProperty, cts);

        try
        {
            var token = cts.Token;
            var lease = disableCache
                ? await LoadUncachedAsync(url, decodeWidth, useSerialDecode, token).ConfigureAwait(false)
                : await AcquireBitmapAsync(url, decodeWidth, useSerialDecode, token).ConfigureAwait(false);
            if (lease == null) return;
            if (token.IsCancellationRequested)
            {
                lease.Dispose();
                return;
            }

            try
            {
                void Apply()
                {
                    if (token.IsCancellationRequested || image.GetValue(CurrentLoadCtsProperty) != cts)
                    {
                        lease.Dispose();
                        return;
                    }

                    AssignImageSource(image, lease);
                }

                if (image.CheckAccess()) Apply();
                else image.Dispatcher.Post(Apply);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            ReportLoadFailure(url, "Load", ex);
        }
    }

    // Narrow dependency overrides let tests exercise the real download/decode path without HTTP servers or UI.
    internal static async Task<Bitmap?> LoadBitmapAsync(
        string source, int? decodeWidth, bool useSerialDecode, CancellationToken cancellationToken,
        HttpClient? httpClient = null, IErrorService? errorService = null)
    {
        var operation = "OpenLocalFile";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                operation = "Download";
                using var buffered = new MemoryStream();
                using (var response = await (httpClient ?? GetHttpClient()).GetAsync(
                           source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaximumHttpImageBytes)
                        throw new ImageLimitExceededException("Image.DownloadTooLarge");

                    await using var network = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await BufferHttpImageAsync(network, buffered, cancellationToken).ConfigureAwait(false);
                }

                operation = "Decode";
                return await DecodeBitmapUnlessCancelledAsync(buffered, decodeWidth, cancellationToken, useSerialDecode)
                    .ConfigureAwait(false);
            }

            if (!File.Exists(source)) return null;
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                DownloadBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            operation = "Decode";
            return await DecodeBitmapUnlessCancelledAsync(file, decodeWidth, cancellationToken, useSerialDecode)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            ReportLoadFailure(source, operation, ex, errorService);
            return null;
        }
    }

    private static async Task BufferHttpImageAsync(Stream source, MemoryStream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferBytes);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Probe at most one byte beyond the limit, and never append that byte to the MemoryStream.
                var count = Math.Min(DownloadBufferBytes, MaximumHttpImageBytes - (int)destination.Length + 1);
                var read = await source.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (read == 0) return;
                if (destination.Length + read > MaximumHttpImageBytes)
                    throw new ImageLimitExceededException("Image.DownloadTooLarge");
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ReportLoadFailure(string source, string operation, Exception exception, IErrorService? errors = null)
    {
        try
        {
            errors ??= App.Current?.Services?.GetService(typeof(IErrorService)) as IErrorService;
            if (errors == null) return;
            var code = exception is ImageLimitExceededException limit ? limit.Code : "Image.LoadFailed";
            var sourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            var key = code + ":" + sourceId;
            lock (FailureReportLock)
            {
                var now = Environment.TickCount64;
                if (LastFailureReports.TryGetValue(key, out var lastReport) && now - lastReport < FailureReportIntervalMs)
                    return;
                if (!LastFailureReports.ContainsKey(key))
                {
                    if (LastFailureReports.Count >= MaximumFailureReportKeys)
                        LastFailureReports.Remove(FailureReportOrder.Dequeue());
                    FailureReportOrder.Enqueue(key);
                }
                LastFailureReports[key] = now;
            }

            // Do not forward raw exception messages/stacks: HTTP failures can contain signed/credential URLs.
            errors.Report(new ErrorReport(code,
                exception is ImageLimitExceededException ? ErrorSeverity.Warning : ErrorSeverity.Diagnostic,
                nameof(AsyncImageHelper), code switch
                {
                    "Image.DownloadTooLarge" => "The HTTP image exceeds the 32 MiB download limit.",
                    "Image.DimensionsExceeded" => "The image exceeds the permitted source or decoded dimensions.",
                    _ => "The image could not be loaded. The placeholder remains visible."
                }, Context: new Dictionary<string, string>
                {
                    ["Operation"] = operation,
                    ["SourceId"] = sourceId,
                    ["SourceKind"] = source.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "HTTP" : "Local",
                    ["FailureType"] = exception.GetType().Name,
                    ["MaximumHttpBytes"] = MaximumHttpImageBytes.ToString(CultureInfo.InvariantCulture),
                    ["MaximumDimension"] = MaximumImageDimension.ToString(CultureInfo.InvariantCulture),
                    ["MaximumPixels"] = MaximumImagePixels.ToString(CultureInfo.InvariantCulture)
                }));
        }
        catch
        {
            // Diagnosis must not change placeholder/cancellation behavior or escape a fire-and-forget load.
        }
    }

    private sealed class ImageLimitExceededException(string code) : Exception("An image resource limit was exceeded.")
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// Warms the shared cache for local artwork without creating a visual. Calls
    /// for the same cache key share one decode operation.
    /// </summary>
    public static async Task PreloadLocalAsync(
        string? path,
        int? decodeWidth = null,
        bool useSerialDecode = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var lease = await AcquireBitmapAsync(path, decodeWidth, useSerialDecode, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportLoadFailure(path, "Preload", ex);
        }
    }

    private static Bitmap DecodeBitmap(Stream stream, int? decodeWidth)
    {
        stream.Position = 0;
        // SKCodec.Info parses metadata without requesting pixels. Explicitly leave the caller's stream open.
        using (var nativeStream = new SKManagedStream(stream, disposeManagedStream: false))
        using (var codec = SKCodec.Create(nativeStream))
        {
            if (codec == null)
                throw new InvalidDataException("The image format is unsupported or invalid.");
            var info = codec.Info;
            ValidateImageDimensions(info.Width, info.Height);
            if (decodeWidth is > 0)
            {
                // Use wide arithmetic, including for thin images that would otherwise upscale to extreme heights.
                var targetHeight = (long)info.Height * decodeWidth.Value / info.Width;
                ValidateImageDimensions(decodeWidth.Value, targetHeight);
            }
        }

        stream.Position = 0;
        if (decodeWidth is > 0)
        {
            return Bitmap.DecodeToWidth(stream, decodeWidth.Value);
        }
        return new Bitmap(stream);
    }

    private static void ValidateImageDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaximumImageDimension || height > MaximumImageDimension ||
            width * height > MaximumImagePixels)
            throw new ImageLimitExceededException("Image.DimensionsExceeded");
    }

    private static Bitmap? DecodeBitmapUnlessCancelled(
        Stream stream,
        int? decodeWidth,
        CancellationToken cancellationToken)
    {
        var bitmap = DecodeBitmap(stream, decodeWidth);
        if (!cancellationToken.IsCancellationRequested)
            return bitmap;

        bitmap.Dispose();
        return null;
    }

    private static async Task<Bitmap?> DecodeBitmapUnlessCancelledAsync(
        Stream stream,
        int? decodeWidth,
        CancellationToken cancellationToken,
        bool useSerialDecode)
    {
        var decodeGate = useSerialDecode ? SerialDecodeGate : DesktopDecodeGate;
        await decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DecodeBitmapUnlessCancelled(stream, decodeWidth, cancellationToken);
        }
        finally
        {
            decodeGate.Release();
        }
    }
    
    private static IImage CreatePlaceholderImage()
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(1, 1),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bitmap.Lock())
        {
            unsafe
            {
                *((uint*)fb.Address) = 0; // transparent
            }
        }

        return bitmap;
    }

    // --- Cache Helpers ---

    public static void InvalidateCache(string url)
    {
        lock (CacheLock)
        {
            // A decode started before an asset edit must never repopulate the cache.
            foreach (var (key, request) in SharedLoads)
            {
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                    request.Invalidated = true;
            }

            var keysToRemove = new List<string>();
            foreach (var key in Cache.Keys)
            {
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                    keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
            {
                if (Cache.TryGetValue(key, out var entry))
                {
                    if (GetCacheRefCount(key) == 0)
                    {
                        RemoveCacheEntry(key);
                    }
                    else
                    {
                        InvalidatedKeys.Add(key);
                    }
                }
            }
        }
    }
    
    private static CacheLease? TryAcquireFromCache(string key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                if (InvalidatedKeys.Contains(key))
                    return null;

                LruList.Remove(entry.Node);
                LruList.AddLast(entry.Node); // Move to MRU position
                IncrementCacheRef(key);
                return new CacheLease(key, entry.Bitmap);
            }
        }
        return null;
    }

    private static CacheLease? TryAcquireAnyFromCacheByUrl(string url)
    {
        lock (CacheLock)
        {
            // Take the most recently used variant (MRU) that matches the URL
            for (var node = LruList.Last; node != null; node = node.Previous)
            {
                var key = node.Value;
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                {
                    if (InvalidatedKeys.Contains(key))
                        continue;

                    if (!Cache.TryGetValue(key, out var entry))
                        continue;

                    LruList.Remove(entry.Node);
                    LruList.AddLast(entry.Node);
                    IncrementCacheRef(key);
                    return new CacheLease(key, entry.Bitmap);
                }
            }

            return null;
        }
    }
    
    private static CacheAddResult AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                if (InvalidatedKeys.Contains(key))
                    return new CacheAddResult(bitmap, IsCached: false, AssignmentLease: null);

                LruList.Remove(existing.Node);
                LruList.AddLast(existing.Node);
                bitmap.Dispose();
                IncrementCacheRef(key);
                return new CacheAddResult(
                    existing.Bitmap,
                    IsCached: true,
                    new CacheLease(key, existing.Bitmap));
            }

            // Decoded pixel storage, excluding GPU textures, codec temporaries and
            // control-owned results. Pinned entries count against both hard limits.
            var bytes = checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
            if (bytes > MaxCacheBytes)
                return new CacheAddResult(bitmap, IsCached: false, AssignmentLease: null);

            while (Cache.Count >= MaxCacheSize || _cachedBitmapBytes > MaxCacheBytes - bytes)
            {
                if (!TryEvictOne())
                    return new CacheAddResult(bitmap, IsCached: false, AssignmentLease: null);
            }

            InvalidatedKeys.Remove(key);

            var node = LruList.AddLast(key);
            Cache[key] = (bitmap, node, bytes);
            _cachedBitmapBytes += bytes;
            IncrementCacheRef(key);
            return new CacheAddResult(
                bitmap,
                IsCached: true,
                new CacheLease(key, bitmap));
        }
    }

    private static void AssignImageSource(Image image, BitmapLease lease)
    {
        var previous = image.GetValue(CurrentBitmapLeaseProperty);
        image.SetValue(CurrentBitmapLeaseProperty, lease);
        image.Source = lease.Bitmap;
        previous?.Dispose();
        image.SetValue(IsLoadedProperty, true);
    }

    private static void ReleaseImageLease(Image image)
    {
        var previous = image.GetValue(CurrentBitmapLeaseProperty);
        image.SetValue(CurrentBitmapLeaseProperty, null);
        previous?.Dispose();
    }

    // A superseded Crossfade target can be canceled without touching either
    // already decoded image participating in the visible transition.
    internal static void CancelPendingLoad(Image image)
    {
        if (!GetIsLoaded(image) && image.GetValue(CurrentLoadCtsProperty) != null)
            SetUrl(image, null);
    }

    private static int GetCacheRefCount(string key)
        => CacheRefCounts.TryGetValue(key, out var count) ? count : 0;

    private static void IncrementCacheRef(string key)
    {
        lock (CacheLock)
        {
            CacheRefCounts[key] = CacheRefCounts.TryGetValue(key, out var count)
                ? count + 1
                : 1;
        }
    }

    private static void DecrementCacheRef(string key)
    {
        lock (CacheLock)
        {
            if (!CacheRefCounts.TryGetValue(key, out var count))
                return;

            count--;
            if (count <= 0)
            {
                CacheRefCounts.Remove(key);

                if (InvalidatedKeys.Contains(key) && Cache.TryGetValue(key, out var entry))
                {
                    RemoveCacheEntry(key);
                }
            }
            else
            {
                CacheRefCounts[key] = count;
            }
        }
    }

    private static bool TryEvictOne()
    {
        var node = LruList.First;
        while (node != null)
        {
            var key = node.Value;
            var next = node.Next;

            if (GetCacheRefCount(key) > 0)
            {
                node = next;
                continue;
            }

            if (Cache.TryGetValue(key, out var entry))
            {
                RemoveCacheEntry(key);
                return true;
            }

            LruList.Remove(node);
            node = next;
        }

        return false;
    }

    private static void RemoveCacheEntry(string key)
    {
        var entry = Cache[key];
        Cache.Remove(key);
        LruList.Remove(entry.Node);
        InvalidatedKeys.Remove(key);
        _cachedBitmapBytes -= entry.Bytes;
        entry.Bitmap.Dispose();
    }

    private static void EnsureDetachHandler(Image image)
    {
        if (image.GetValue(DetachHandlerAttachedProperty))
            return;

        image.SetValue(DetachHandlerAttachedProperty, true);
        image.AttachedToVisualTree += OnImageAttached;
        image.DetachedFromVisualTree += OnImageDetached;
    }

    private static void OnImageAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image image)
            return;

        if (image.GetValue(CurrentLoadCtsProperty) != null)
            return;

        var url = GetUrl(image);
        if (string.IsNullOrEmpty(url))
            return;

        if (image.Source != null && !ReferenceEquals(image.Source, PlaceholderImage))
            return;

        var width = GetDecodeWidth(image);
        var disableCache = GetDisableCache(image);
        var useSerialDecode = GetUseSerialDecode(image);
        _ = LoadImageAsync(image, url, width, disableCache, useSerialDecode);
    }

    private static void OnImageDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image)
            ResetImage(image);
    }

    /// <summary>
    /// Saves a cached image to disk.
    /// </summary>
    public static async Task<bool> SaveCachedImageAsync(string url, string destinationPath)
    {
        using var lease = TryAcquireAnyFromCacheByUrl(url);
        if (lease == null) return false;

        return await Task.Run(() =>
        {
            try
            {
                lease.Bitmap.Save(destinationPath, new PngBitmapEncoderOptions());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsyncImageHelper] Save error: {ex.Message}");
                return false;
            }
        });
    }
}
