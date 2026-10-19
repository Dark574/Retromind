using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Achievements;

public sealed class StoreAchievementImageCacheService : IStoreAchievementImageCacheService
{
    private const int MaximumImageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ImageRetention = TimeSpan.FromDays(180);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan LastUseTouchInterval = TimeSpan.FromDays(7);
    private readonly HttpClient _httpClient;
    private readonly StoreAchievementCachePathProvider _cachePathProvider;
    private readonly object _cleanupLock = new();
    private readonly DateTimeOffset[] _lastCleanupUtc =
        [DateTimeOffset.MinValue, DateTimeOffset.MinValue];

    public StoreAchievementImageCacheService(
        HttpClient httpClient,
        StoreAchievementCachePathProvider cachePathProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cachePathProvider = cachePathProvider ??
            throw new ArgumentNullException(nameof(cachePathProvider));
    }

    public async Task<string?> GetImagePathAsync(
        StoreAchievementProvider provider,
        string? imageUrl,
        CancellationToken cancellationToken = default)
    {
        if (provider is not (StoreAchievementProvider.Steam or StoreAchievementProvider.Gog))
            return null;

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var cacheDirectory = _cachePathProvider.GetImageCacheDirectory(provider);
        var now = DateTimeOffset.UtcNow;
        TryCleanupUnusedImages(provider, cacheDirectory, now);
        var cachePath = Path.Combine(cacheDirectory, GetUrlHash(uri) + ".image");
        if (await IsValidCachedImageAsync(cachePath, cancellationToken).ConfigureAwait(false))
        {
            TryTouchLastUse(cachePath, now);
            return cachePath;
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporaryPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await _httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!response.IsSuccessStatusCode ||
                finalUri.Scheme != Uri.UriSchemeHttps ||
                response.Content.Headers.ContentLength is > MaximumImageBytes)
            {
                return null;
            }

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var totalBytes = 0;
                    while (true)
                    {
                        var bytesRead = await source
                            .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        totalBytes = checked(totalBytes + bytesRead);
                        if (totalBytes > MaximumImageBytes)
                            return null;

                        await destination
                            .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (!await IsValidCachedImageAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
                return null;

            File.Move(temporaryPath, cachePath, overwrite: true);
            return cachePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed optional image cleanup must not hide achievement progress.
            }
        }
    }

    private static string GetUrlHash(Uri uri)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        return Convert.ToHexStringLower(bytes);
    }

    private void TryCleanupUnusedImages(
        StoreAchievementProvider provider,
        string cacheDirectory,
        DateTimeOffset now)
    {
        var providerIndex = (int)provider;
        lock (_cleanupLock)
        {
            if (now - _lastCleanupUtc[providerIndex] < CleanupInterval)
                return;

            _lastCleanupUtc[providerIndex] = now;
        }

        try
        {
            var directory = new DirectoryInfo(cacheDirectory);
            if (!directory.Exists ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            var cutoff = now - ImageRetention;
            foreach (var file in directory.EnumerateFiles("*.image", SearchOption.TopDirectoryOnly))
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    file.LastWriteTimeUtc >= cutoff.UtcDateTime)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch
                {
                    // Cache cleanup is best effort and must not affect progress.
                }
            }
        }
        catch
        {
            // Cache cleanup is best effort and must not affect progress.
        }
    }

    private static void TryTouchLastUse(string cachePath, DateTimeOffset now)
    {
        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(cachePath);
            if (now.UtcDateTime - lastWriteUtc >= LastUseTouchInterval)
                File.SetLastWriteTimeUtc(cachePath, now.UtcDateTime);
        }
        catch
        {
            // A timestamp update is optional; the valid image can still be used.
        }
    }

    private static async Task<bool> IsValidCachedImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 8 or > MaximumImageBytes)
                return false;

            var signature = new byte[12];
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                signature.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await stream
                .ReadAsync(signature.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            return IsPng(signature, bytesRead) ||
                   IsJpeg(signature, bytesRead) ||
                   IsGif(signature, bytesRead) ||
                   IsWebP(signature, bytesRead);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPng(byte[] bytes, int length) =>
        length >= 8 &&
        bytes.AsSpan(0, 8).SequenceEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool IsJpeg(byte[] bytes, int length) =>
        length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;

    private static bool IsGif(byte[] bytes, int length) =>
        length >= 6 &&
        (bytes.AsSpan(0, 6).SequenceEqual("GIF87a"u8) ||
         bytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8));

    private static bool IsWebP(byte[] bytes, int length) =>
        length >= 12 &&
        bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
        bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
}
