using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class PrefixPathHelperTests
{
    [Fact]
    public void EnsureDosDeviceMapping_CreatesMappingAndPreservesExistingTarget()
    {
        using var temp = new TemporaryDirectory();
        var dosDevicesPath = temp.CreateDirectory("prefix", "dosdevices");
        temp.CreateDirectory("prefix", "drive_c");

        PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesPath, "c:", "../drive_c");
        PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesPath, "c:", "../different-target");

        var linkPath = Path.Combine(dosDevicesPath, "c:");
        Assert.True(Directory.Exists(linkPath));
        Assert.Equal("../drive_c", new DirectoryInfo(linkPath).LinkTarget);
    }

    [Fact]
    public void TryMakeLibraryRelative_ConvertsPrefixInsideLibraryRoot()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = temp.GetPath("Library", "Prefixes", "Game_Prefix");

        var converted = PrefixPathHelper.TryMakeLibraryRelativeIfInsideLibraryRoot(
            prefixPath,
            libraryRoot,
            out var relativePath);

        Assert.True(converted);
        Assert.Equal(Path.Combine("Prefixes", "Game_Prefix"), relativePath);
    }

    [Fact]
    public void TryMakeLibraryRelative_RejectsSiblingAndDifferentlyCasedRoots()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var siblingPath = temp.GetPath("Library-backup", "Prefix");
        var differentlyCasedPath = temp.GetPath("library", "Prefixes", "Game");

        Assert.False(PrefixPathHelper.TryMakeLibraryRelativeIfInsideLibraryRoot(
            siblingPath,
            libraryRoot,
            out _));
        Assert.False(PrefixPathHelper.TryMakeLibraryRelativeIfInsideLibraryRoot(
            differentlyCasedPath,
            libraryRoot,
            out _));
    }

    [Fact]
    public void ConvertPathToLibraryRelative_PreservesAlreadyRelativePath()
    {
        using var temp = new TemporaryDirectory();
        var relativePath = Path.Combine("Prefixes", "Existing");

        var converted = PrefixPathHelper.ConvertPathToLibraryRelativeIfInsideLibraryRoot(
            relativePath,
            temp.GetPath("Library"));

        Assert.Equal(relativePath, converted);
    }
}
