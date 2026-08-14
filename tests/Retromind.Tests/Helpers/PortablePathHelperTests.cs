using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class PortablePathHelperTests
{
    [Fact]
    public void TryMakeDataRelative_ConvertsPathInsidePortableRoot()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var absolutePath = temp.GetPath("Library", "Games", "Grüße aus Köln", "game.exe");

        var converted = PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(
            absolutePath,
            out var relativePath);

        Assert.True(converted);
        Assert.Equal(Path.Combine("Library", "Games", "Grüße aus Köln", "game.exe"), relativePath);
    }

    [Fact]
    public void TryMakeDataRelative_RejectsSiblingAndDifferentlyCasedRoots()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var siblingPath = Path.Combine(temp.RootPath + "-backup", "game.exe");
        var differentlyCasedRoot = Path.Combine(
            Path.GetDirectoryName(temp.RootPath)!,
            Path.GetFileName(temp.RootPath).ToUpperInvariant());
        var differentlyCasedPath = Path.Combine(differentlyCasedRoot, "game.exe");

        Assert.False(PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(siblingPath, out _));
        Assert.False(PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(differentlyCasedPath, out _));
    }

    [Fact]
    public void ConvertPathToPortable_PreservesRelativeAndExternalPaths()
    {
        using var temp = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var relativePath = Path.Combine("Emulators", "umu-run");
        var externalPath = external.GetPath("wine");

        Assert.Equal(
            relativePath,
            PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(relativePath));
        Assert.Equal(
            externalPath,
            PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(externalPath));
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
