using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class PrefixPathHelperTests
{
    [Theory]
    [InlineData("Shadow Tactics: Blades/of the Shogun", "Shadow_Tactics_Blades_of_the_Shogun")]
    [InlineData("A <portable> game?*", "A_portable_game")]
    [InlineData("Game...", "Game")]
    [InlineData("CON", "_CON")]
    [InlineData("Lpt9.txt", "_Lpt9.txt")]
    [InlineData("...", "Unknown")]
    public void SanitizePrefixFolderName_ProducesPortableNames(string input, string expected)
    {
        Assert.Equal(expected, PrefixPathHelper.SanitizePrefixFolderName(input));
    }

    [Fact]
    public void SanitizePrefixFolderName_TruncatesAtUtf8RuneBoundary()
    {
        var sanitized = PrefixPathHelper.SanitizePrefixFolderName(new string('ä', 60));

        Assert.Equal(80, System.Text.Encoding.UTF8.GetByteCount(sanitized));
        Assert.Equal(40, sanitized.Length);
    }

    [Fact]
    public void ResolveAbsolutePrefixPath_NormalizesRelativeTrailingSeparators()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var expected = temp.GetPath("Library", "Prefixes");

        var variants = new[]
        {
            "Prefixes",
            "Prefixes/",
            "Prefixes//"
        };

        foreach (var variant in variants)
        {
            Assert.Equal(
                expected,
                PrefixPathHelper.ResolveAbsolutePrefixPath(variant, libraryRoot));
        }
    }

    [Fact]
    public void ResolveAbsolutePrefixPath_NormalizesAbsoluteTrailingSeparators()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var expected = temp.GetPath("External", "Prefixes");
        var variants = new[]
        {
            expected,
            expected + Path.DirectorySeparatorChar,
            expected + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar
        };

        foreach (var variant in variants)
        {
            Assert.Equal(
                expected,
                PrefixPathHelper.ResolveAbsolutePrefixPath(variant, libraryRoot));
        }
    }

    [Fact]
    public void ResolveAbsolutePrefixPath_NormalizesRelativeDotSegments()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var expected = temp.GetPath("Library", "Prefixes", "Game");
        var variants = new[]
        {
            "Prefixes/./Game/",
            "Prefixes/Nested/../Game//"
        };

        foreach (var variant in variants)
        {
            Assert.Equal(
                expected,
                PrefixPathHelper.ResolveAbsolutePrefixPath(variant, libraryRoot));
        }
    }

    [Fact]
    public void ResolveAbsolutePrefixPath_PreservesPfxLeafDetection()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var expected = temp.GetPath("Library", "Prefixes", "Game", "pfx");

        foreach (var variant in new[] { "Prefixes/Game/pfx", "Prefixes/Game/pfx/", "Prefixes/Game/pfx//" })
        {
            var resolved = PrefixPathHelper.ResolveAbsolutePrefixPath(variant, libraryRoot);

            Assert.Equal(expected, resolved);
            Assert.True(PrefixPathHelper.IsPfxPath(resolved));
        }
    }

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
    public void EnsurePortableGamesDriveMapping_UsesPreferredFreeDrive()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.CreateDirectory("Library", "Prefixes", "Game");
        var dosDevices = temp.CreateDirectory("Library", "Prefixes", "Game", "dosdevices");

        var drive = PrefixPathHelper.EnsurePortableGamesDriveMapping(prefixRoot, dosDevices, libraryRoot);

        Assert.Equal("R", drive);
        AssertMappingTargets(Path.Combine(dosDevices, "r:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void EnsurePortableGamesDriveMapping_MigratesOwnedMappingShadowedByRawDevice()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var gamesRoot = temp.CreateDirectory("Library", "Games");
        var prefixRoot = temp.CreateDirectory("Library", "Prefixes", "Game");
        var dosDevices = temp.CreateDirectory("Library", "Prefixes", "Game", "dosdevices");
        File.CreateSymbolicLink(Path.Combine(dosDevices, "d:"), Path.GetRelativePath(dosDevices, gamesRoot));
        File.CreateSymbolicLink(Path.Combine(dosDevices, "d::"), "/dev/sda1");

        var drive = PrefixPathHelper.EnsurePortableGamesDriveMapping(prefixRoot, dosDevices, libraryRoot);

        Assert.Equal("R", drive);
        Assert.False(File.Exists(Path.Combine(dosDevices, "d:")));
        Assert.Null(new FileInfo(Path.Combine(dosDevices, "d:")).LinkTarget);
        Assert.NotNull(new FileInfo(Path.Combine(dosDevices, "d::")).LinkTarget);
        AssertMappingTargets(Path.Combine(dosDevices, "r:"), gamesRoot);
    }

    [Fact]
    public void EnsurePortableGamesDriveMapping_PreservesUserMappingAndUsesFallback()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.CreateDirectory("Library", "Prefixes", "Game");
        var dosDevices = temp.CreateDirectory("Library", "Prefixes", "Game", "dosdevices");
        File.CreateSymbolicLink(Path.Combine(dosDevices, "r:"), "/custom/location");

        var drive = PrefixPathHelper.EnsurePortableGamesDriveMapping(prefixRoot, dosDevices, libraryRoot);

        Assert.Equal("Q", drive);
        Assert.Equal("/custom/location", new FileInfo(Path.Combine(dosDevices, "r:")).LinkTarget);
        AssertMappingTargets(Path.Combine(dosDevices, "q:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void EnsurePortableGamesDriveMapping_DoesNotModifyExternalPrefix()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.CreateDirectory("External", "Prefix");
        var dosDevices = temp.GetPath("External", "Prefix", "dosdevices");

        var drive = PrefixPathHelper.EnsurePortableGamesDriveMapping(prefixRoot, dosDevices, libraryRoot);

        Assert.Null(drive);
        Assert.False(Directory.Exists(dosDevices));
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Games")));
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

    private static void AssertMappingTargets(string mappingPath, string expectedTarget)
    {
        var linkTarget = new DirectoryInfo(mappingPath).LinkTarget;
        Assert.NotNull(linkTarget);
        Assert.Equal(
            Path.GetFullPath(expectedTarget),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mappingPath)!, linkTarget)));
    }
}
