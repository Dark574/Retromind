using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class PortableEnvironmentTests
{
    [Fact]
    public void GetConfiguredPortableCacheRoot_EnabledForAppImage_UsesPortableHome()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        File.WriteAllText(
            temp.GetPath("app_settings.json"),
            """{"UsePortableHomeInAppImage":true}""");

        var cacheRoot = PortableEnvironment.GetConfiguredPortableCacheRoot();

        Assert.Equal(Path.Combine(temp.RootPath, "Home", ".cache"), cacheRoot);
    }

    [Fact]
    public void GetConfiguredPortableCacheRoot_DisabledForAppImage_ReturnsNull()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        File.WriteAllText(
            temp.GetPath("app_settings.json"),
            """{"UsePortableHomeInAppImage":false}""");

        Assert.Null(PortableEnvironment.GetConfiguredPortableCacheRoot());
    }

    [Fact]
    public void GetConfiguredPortableCacheRoot_OutsideAppImage_ReturnsNull()
    {
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", null),
            ("APPDIR", null));

        Assert.Null(PortableEnvironment.GetConfiguredPortableCacheRoot());
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
