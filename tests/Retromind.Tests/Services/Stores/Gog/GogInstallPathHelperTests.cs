using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallPathHelperTests
{
    [Fact]
    public void ToStoredPath_PortableInstallInsideDataRoot_BecomesRelative()
    {
        using var portableRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var installPath = portableRoot.GetPath("Library", "Games", "GOG", "Portable Game");

        var storedPath = GogInstallPathHelper.ToStoredPath(
            installPath,
            preferPortablePath: true);

        Assert.Equal(Path.Combine("Library", "Games", "GOG", "Portable Game"), storedPath);
    }

    [Fact]
    public void ToStoredPath_ExternalOrDisabledPortablePath_RemainsAbsolute()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var internalPath = portableRoot.GetPath("Library", "Games", "GOG", "Internal");
        var externalPath = externalRoot.GetPath("External GOG Game");

        Assert.Equal(
            internalPath,
            GogInstallPathHelper.ToStoredPath(internalPath, preferPortablePath: false));
        Assert.Equal(
            externalPath,
            GogInstallPathHelper.ToStoredPath(externalPath, preferPortablePath: true));
    }

    [Fact]
    public void TryResolveStoredPath_RelativeInstallRebindsAfterRootMove()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        var relativePath = Path.Combine("Library", "Games", "GOG", "Portable Game");

        using (UseDataRoot(firstRoot.RootPath))
        {
            Assert.True(GogInstallPathHelper.TryResolveStoredPath(relativePath, out var firstResolved));
            Assert.Equal(firstRoot.GetPath(relativePath), firstResolved);
        }

        using (UseDataRoot(secondRoot.RootPath))
        {
            Assert.True(GogInstallPathHelper.TryResolveStoredPath(relativePath, out var secondResolved));
            Assert.Equal(secondRoot.GetPath(relativePath), secondResolved);
        }
    }

    [Fact]
    public void TryResolveStoredPath_LegacyExternalAbsolutePath_RemainsSupported()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var legacyPath = externalRoot.GetPath("Legacy GOG Game");

        var resolved = GogInstallPathHelper.TryResolveStoredPath(legacyPath, out var fullPath);

        Assert.True(resolved);
        Assert.Equal(legacyPath, fullPath);
    }

    [Fact]
    public void TryResolveStoredPath_RelativeTraversal_IsRejected()
    {
        using var portableRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);

        var resolved = GogInstallPathHelper.TryResolveStoredPath(
            Path.Combine("..", "outside", "game"),
            out var fullPath);

        Assert.False(resolved);
        Assert.Empty(fullPath);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
