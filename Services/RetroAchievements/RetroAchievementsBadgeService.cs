using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Downloads immutable RetroAchievements badge images into the shared persistent
/// RetroAchievements cache. UI layers receive local paths and remain independent
/// of network and filesystem concerns.
/// </summary>
public sealed class RetroAchievementsBadgeService : IRetroAchievementsBadgeService
{
    private const int MaximumBadgeBytes = 1024 * 1024;
    private static readonly Uri BadgeBaseUri = new("https://i.retroachievements.org/Badge/");
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly HttpClient _httpClient;
    private readonly Func<string> _cacheDirectoryProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates =
        new(StringComparer.Ordinal);

    public RetroAchievementsBadgeService(
        HttpClient httpClient,
        RetroAchievementsCachePathProvider cachePathProvider)
        : this(
            httpClient,
            cachePathProvider == null
                ? throw new ArgumentNullException(nameof(cachePathProvider))
                : cachePathProvider.GetBadgeCacheDirectory)
    {
    }

    internal RetroAchievementsBadgeService(HttpClient httpClient, string cacheDirectory)
        : this(httpClient, () => Path.GetFullPath(cacheDirectory))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    }

    private RetroAchievementsBadgeService(
        HttpClient httpClient,
        Func<string> cacheDirectoryProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheDirectoryProvider = cacheDirectoryProvider ??
                                  throw new ArgumentNullException(nameof(cacheDirectoryProvider));
    }

    public async Task<string?> GetBadgePathAsync(
        string? badgeName,
        bool isUnlocked,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedBadgeName = NormalizeBadgeName(badgeName);
            if (normalizedBadgeName == null)
                return null;

            var fileStem = isUnlocked
                ? normalizedBadgeName
                : normalizedBadgeName + "_lock";
            var cacheDirectory = _cacheDirectoryProvider();
            var cachePath = Path.Combine(cacheDirectory, fileStem + ".png");
            if (await IsValidPngAsync(cachePath, cancellationToken).ConfigureAwait(false))
                return cachePath;

            var gate = _downloadGates.GetOrAdd(fileStem, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await IsValidPngAsync(cachePath, cancellationToken).ConfigureAwait(false))
                    return cachePath;

                TryDeleteFile(cachePath);
                return await TryDownloadBadgeAsync(
                        fileStem,
                        cachePath,
                        cacheDirectory,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ? cachePath
                    : null;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Badge images are optional presentation data. A selection change should
            // stop their work quietly instead of surfacing a cancellation exception.
            return null;
        }
    }

    private async Task<bool> TryDownloadBadgeAsync(
        string fileStem,
        string cachePath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var requestUri = new Uri(BadgeBaseUri, Uri.EscapeDataString(fileStem) + ".png");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine(
                    $"[RetroAchievements] Badge download returned HTTP {(int)response.StatusCode} for '{fileStem}'.");
                return false;
            }

            if (response.Content.Headers.ContentLength is > MaximumBadgeBytes)
            {
                Debug.WriteLine(
                    $"[RetroAchievements] Badge '{fileStem}' exceeds the download size limit.");
                return false;
            }

            Directory.CreateDirectory(cacheDirectory);
            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[16 * 1024];
                var totalBytes = 0;
                while (true)
                {
                    var bytesRead = await source
                        .ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    totalBytes += bytesRead;
                    if (totalBytes > MaximumBadgeBytes)
                        throw new InvalidDataException("The badge image exceeds the download size limit.");

                    await destination
                        .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!await IsValidPngAsync(tempPath, cancellationToken).ConfigureAwait(false))
            {
                Debug.WriteLine(
                    $"[RetroAchievements] Badge '{fileStem}' did not contain a valid PNG signature.");
                return false;
            }

            File.Move(tempPath, cachePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or
                                   OperationCanceledException or
                                   IOException or
                                   UnauthorizedAccessException)
        {
            Debug.WriteLine(
                $"[RetroAchievements] Could not cache badge '{fileStem}': {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static async Task<bool> IsValidPngAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var info = new FileInfo(path);
            if (info.Length < PngSignature.Length || info.Length > MaximumBadgeBytes)
                return false;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                PngSignature.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var signature = new byte[PngSignature.Length];
            var bytesRead = await stream
                .ReadAsync(signature, cancellationToken)
                .ConfigureAwait(false);
            return bytesRead == PngSignature.Length &&
                   signature.AsSpan().SequenceEqual(PngSignature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(
                $"[RetroAchievements] Could not inspect cached badge '{path}': {ex.Message}");
            return false;
        }
    }

    private static string? NormalizeBadgeName(string? badgeName)
    {
        if (string.IsNullOrWhiteSpace(badgeName))
            return null;

        var normalized = badgeName.Trim();
        if (normalized.Length > 128)
            return null;

        foreach (var character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return null;
        }

        return normalized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort. A unique temp path is used for every download.
        }
    }
}
