using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class AppPathsTests
{
    [Fact]
    public void DataRoot_WithAppImage_UsesDirectoryContainingAppImage()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);

        Assert.Equal(temp.RootPath, AppPaths.DataRoot);
        Assert.Equal(Path.Combine(temp.RootPath, "Library"), AppPaths.LibraryRoot);
        Assert.Equal(Path.Combine(temp.RootPath, "Themes"), AppPaths.ThemesRoot);
    }

    [Fact]
    public void TryResolveDataPathInsideRoot_AcceptsRelativeAndAbsolutePathsInsideRoot()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var relativePath = Path.Combine("Library", "Games", "Müller Test", "game.bin");
        var expectedPath = Path.Combine(temp.RootPath, relativePath);

        Assert.True(AppPaths.TryResolveDataPathInsideRoot(relativePath, out var resolvedRelative));
        Assert.Equal(expectedPath, resolvedRelative);
        Assert.True(AppPaths.TryResolveDataPathInsideRoot(expectedPath, out var resolvedAbsolute));
        Assert.Equal(expectedPath, resolvedAbsolute);
    }

    [Fact]
    public void TryResolveDataPathInsideRoot_RejectsTraversalAndSiblingPaths()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var siblingPath = temp.RootPath + "-backup";

        Assert.False(AppPaths.TryResolveDataPathInsideRoot("../outside.txt", out _));
        Assert.False(AppPaths.TryResolveDataPathInsideRoot(siblingPath, out _));
    }

    [Fact]
    public void TryResolveDataPathInsideRoot_TreatsLinuxPathCasingAsDistinct()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var differentlyCasedRoot = Path.Combine(
            Path.GetDirectoryName(temp.RootPath)!,
            Path.GetFileName(temp.RootPath).ToUpperInvariant());
        var candidate = Path.Combine(differentlyCasedRoot, "Library", "game.bin");

        Assert.False(AppPaths.TryResolveDataPathInsideRoot(candidate, out _));
    }

    [Fact]
    public void LibraryRelativeMediaPath_RebindsWhenPortableRootMoves()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        var relativePath = Path.Combine("Library", "Games", "Portable Game", "game.exe");
        var item = new MediaItem
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.LibraryRelative,
                    Path = relativePath
                }
            ]
        };

        using (UseDataRoot(firstRoot.RootPath))
        {
            Assert.Equal(Path.Combine(firstRoot.RootPath, relativePath), item.GetPrimaryLaunchPath());
        }

        using (UseDataRoot(secondRoot.RootPath))
        {
            Assert.Equal(Path.Combine(secondRoot.RootPath, relativePath), item.GetPrimaryLaunchPath());
        }
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
