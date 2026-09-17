using System;
using System.IO;
using System.Threading;
using Retromind.Models;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Owns the active RetroAchievements cache root so long-lived services can
/// follow portable-HOME changes without being recreated.
/// </summary>
public sealed class RetroAchievementsCachePathProvider
{
    private string _cacheDirectory;

    public RetroAchievementsCachePathProvider(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _cacheDirectory = ResolveCacheDirectory(settings.UsePortableHomeInAppImage);
    }

    internal RetroAchievementsCachePathProvider(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    internal string GetCacheDirectory() => Volatile.Read(ref _cacheDirectory);

    internal string GetProgressCacheDirectory() =>
        Path.Combine(GetCacheDirectory(), "Progress");

    internal string GetBadgeCacheDirectory() =>
        Path.Combine(GetCacheDirectory(), "Badges");

    internal bool UsePortableHome(bool enabled)
    {
        var cacheDirectory = ResolveCacheDirectory(enabled);
        var previous = Interlocked.Exchange(ref _cacheDirectory, cacheDirectory);
        return !string.Equals(previous, cacheDirectory, StringComparison.Ordinal);
    }

    internal void SetCacheDirectory(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        Interlocked.Exchange(ref _cacheDirectory, Path.GetFullPath(cacheDirectory));
    }

    private static string ResolveCacheDirectory(bool usePortableHome) =>
        Path.GetFullPath(
            RetroAchievementsGameCatalogService.GetCacheDirectory(usePortableHome));
}
