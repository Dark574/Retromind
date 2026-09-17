using Retromind.Models;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsCachePathProviderTests
{
    [Fact]
    public void Constructor_UsesCurrentPortableHomeSetting()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var settings = new AppSettings { UsePortableHomeInAppImage = true };

        var provider = new RetroAchievementsCachePathProvider(settings);

        Assert.Equal(
            Path.Combine(temp.RootPath, "Home", ".cache", "retromind", "RetroAchievements"),
            provider.GetCacheDirectory());
    }

    [Fact]
    public void UsePortableHome_SwitchesActiveCacheRootInBothDirections()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var provider = new RetroAchievementsCachePathProvider(
            new AppSettings { UsePortableHomeInAppImage = false });

        Assert.Equal(
            Path.Combine(temp.RootPath, "Cache", "RetroAchievements"),
            provider.GetCacheDirectory());
        Assert.True(provider.UsePortableHome(enabled: true));
        Assert.Equal(
            Path.Combine(temp.RootPath, "Home", ".cache", "retromind", "RetroAchievements"),
            provider.GetCacheDirectory());
        Assert.True(provider.UsePortableHome(enabled: false));
        Assert.Equal(
            Path.Combine(temp.RootPath, "Cache", "RetroAchievements"),
            provider.GetCacheDirectory());
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath) =>
        new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
