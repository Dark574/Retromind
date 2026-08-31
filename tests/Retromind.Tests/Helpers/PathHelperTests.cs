using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class PathHelperTests
{
    [Fact]
    public void ResolveNodeFolder_PreservesExistingLegacyFolderInsideLibraryRoot()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var legacyFolder = temp.CreateDirectory("Library", "Legacy Name");

        var resolved = PathHelper.ResolveNodeFolder(["Legacy Name"], libraryRoot);

        Assert.Equal(legacyFolder, resolved);
    }

    [Fact]
    public void ResolveNodeFolder_RejectsExistingTraversalTargetOutsideLibraryRoot()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        temp.CreateDirectory("Outside", "Legacy");
        var expected = Path.Combine(libraryRoot, "Unknown", "Outside", "Legacy");

        var resolved = PathHelper.ResolveNodeFolder(
            ["..", "Outside", "Legacy"],
            libraryRoot);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveNodeFolder_OnLinuxRejectsDifferentlyCasedSiblingRoot()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        temp.CreateDirectory("library", "Legacy");
        var expected = Path.Combine(libraryRoot, "Unknown", "library", "Legacy");

        var resolved = PathHelper.ResolveNodeFolder(
            ["..", "library", "Legacy"],
            libraryRoot);

        Assert.Equal(expected, resolved);
    }
}
