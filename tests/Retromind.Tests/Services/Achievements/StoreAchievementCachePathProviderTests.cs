using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Achievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Achievements;

public sealed class StoreAchievementCachePathProviderTests
{
    [Fact]
    public void UsePortableHome_SwitchesSteamAndGogCacheRoots()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var provider = new StoreAchievementCachePathProvider(
            new AppSettings { UsePortableHomeInAppImage = false });
        var standardSteam = provider.GetSteamProgressCacheDirectory();
        var standardGog = provider.GetGogProgressCacheDirectory();

        Assert.True(provider.UsePortableHome(enabled: true));

        Assert.NotEqual(standardSteam, provider.GetSteamProgressCacheDirectory());
        Assert.NotEqual(standardGog, provider.GetGogProgressCacheDirectory());
        Assert.Contains(
            Path.Combine("Home", ".cache", "retromind", "StoreAchievements"),
            provider.GetSteamProgressCacheDirectory(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigrateCacheLocationAsync_MovesSteamAndGogCaches()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var standard = new StoreAchievementCachePathProvider(
            new AppSettings { UsePortableHomeInAppImage = false });
        var portable = new StoreAchievementCachePathProvider(
            new AppSettings { UsePortableHomeInAppImage = true });
        var sourceSteam = Path.Combine(
            standard.GetSteamProgressCacheDirectory(),
            "account-hash",
            "app-123.json");
        var sourceGog = Path.Combine(
            standard.GetGogProgressCacheDirectory(),
            "account-hash",
            "game-456.json");
        var destinationSteam = Path.Combine(
            portable.GetSteamProgressCacheDirectory(),
            "account-hash",
            "app-123.json");
        var destinationGog = Path.Combine(
            portable.GetGogProgressCacheDirectory(),
            "account-hash",
            "game-456.json");
        var sourceImage = Path.Combine(
            standard.GetImageCacheDirectory(StoreAchievementProvider.Steam),
            "image-hash.image");
        var destinationImage = Path.Combine(
            portable.GetImageCacheDirectory(StoreAchievementProvider.Steam),
            "image-hash.image");
        var sourceSchema = Path.Combine(
            standard.GetSteamSchemaCacheDirectory(),
            "language-hash",
            "app-123.json");
        var destinationSchema = Path.Combine(
            portable.GetSteamSchemaCacheDirectory(),
            "language-hash",
            "app-123.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceSteam)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceGog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceImage)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceSchema)!);
        await File.WriteAllTextAsync(sourceSteam, "steam-cache");
        await File.WriteAllTextAsync(sourceGog, "gog-cache");
        await File.WriteAllTextAsync(sourceImage, "image-cache");
        await File.WriteAllTextAsync(sourceSchema, "schema-cache");

        await StoreAchievementCachePathProvider.MigrateCacheLocationAsync(
            wasPortableHomeEnabled: false,
            isPortableHomeEnabled: true);

        Assert.False(File.Exists(sourceSteam));
        Assert.False(File.Exists(sourceGog));
        Assert.False(File.Exists(sourceImage));
        Assert.False(File.Exists(sourceSchema));
        Assert.Equal("steam-cache", await File.ReadAllTextAsync(destinationSteam));
        Assert.Equal("gog-cache", await File.ReadAllTextAsync(destinationGog));
        Assert.Equal("image-cache", await File.ReadAllTextAsync(destinationImage));
        Assert.Equal("schema-cache", await File.ReadAllTextAsync(destinationSchema));
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath) =>
        new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
