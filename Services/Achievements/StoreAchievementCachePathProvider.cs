using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Achievements;

/// <summary>
/// Owns the Steam and GOG achievement-cache root so long-lived services can
/// follow portable-HOME changes without being recreated.
/// </summary>
public sealed class StoreAchievementCachePathProvider
{
    private static readonly IReadOnlySet<string> CacheExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", ".image" };

    private string _cacheDirectory;

    public StoreAchievementCachePathProvider(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _cacheDirectory = ResolveCacheDirectory(settings.UsePortableHomeInAppImage);
    }

    internal StoreAchievementCachePathProvider(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    internal string GetSteamProgressCacheDirectory() =>
        Path.Combine(GetCacheDirectory(), "Steam", "Progress");

    internal string GetSteamSchemaCacheDirectory() =>
        Path.Combine(GetCacheDirectory(), "Steam", "Schemas");

    internal string GetGogProgressCacheDirectory() =>
        Path.Combine(GetCacheDirectory(), "Gog", "Progress");

    internal string GetImageCacheDirectory(StoreAchievementProvider provider) =>
        Path.Combine(
            GetCacheDirectory(),
            provider == StoreAchievementProvider.Steam ? "Steam" : "Gog",
            "Images");

    internal bool UsePortableHome(bool enabled)
    {
        var cacheDirectory = ResolveCacheDirectory(enabled);
        var previous = Interlocked.Exchange(ref _cacheDirectory, cacheDirectory);
        return !string.Equals(previous, cacheDirectory, StringComparison.Ordinal);
    }

    internal static async Task MigrateCacheLocationAsync(
        bool wasPortableHomeEnabled,
        bool isPortableHomeEnabled,
        CancellationToken cancellationToken = default)
    {
        if (wasPortableHomeEnabled == isPortableHomeEnabled)
            return;

        await CacheDirectoryMigrationHelper.MigrateAsync(
                ResolveCacheDirectory(wasPortableHomeEnabled),
                ResolveCacheDirectory(isPortableHomeEnabled),
                CacheExtensions,
                "StoreAchievements",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string GetCacheDirectory() => Volatile.Read(ref _cacheDirectory);

    private static string ResolveCacheDirectory(bool usePortableHome)
    {
        var portableCacheRoot = PortableEnvironment.GetPortableCacheRoot(usePortableHome);
        return Path.GetFullPath(
            portableCacheRoot != null
                ? Path.Combine(portableCacheRoot, "retromind", "StoreAchievements")
                : Path.Combine(AppPaths.DataRoot, "Cache", "StoreAchievements"));
    }
}
