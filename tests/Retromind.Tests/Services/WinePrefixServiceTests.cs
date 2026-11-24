using System.Diagnostics;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class WinePrefixServiceTests
{
    [Fact]
    public void Inspect_DistinguishesAnIncompleteTreeFromAnInitializedNestedPrefix()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.CreateDirectory("prefix");
        var nested = temp.CreateDirectory("prefix", "pfx");
        temp.CreateDirectory("prefix", "drive_c");
        temp.CreateDirectory("prefix", "pfx", "drive_c", "windows");
        temp.CreateFile("prefix/pfx/system.reg");
        temp.CreateFile("prefix/pfx/user.reg");

        var flat = WinePrefixService.Inspect(root, root, WinePrefixRuntime.Wine);
        var proton = WinePrefixService.Inspect(root, nested, WinePrefixRuntime.Proton);

        Assert.True(flat.HasSeparateTrees);
        Assert.Equal(PrefixTreeState.Incomplete, flat.State);
        Assert.True(proton.HasSeparateTrees);
        Assert.Equal(PrefixTreeState.Initialized, proton.State);
    }

    [Fact]
    public void Inspect_ResolvesRelativeAliasesAndReportsExternalAndBrokenProfileFolders()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.CreateDirectory("prefix");
        var nested = Path.Combine(root, "pfx");
        Directory.CreateSymbolicLink(nested, ".");
        var profile = temp.CreateDirectory("prefix", "drive_c", "users", "someone");
        Directory.CreateSymbolicLink(Path.Combine(profile, "..", "steamuser"), "someone");
        var documents = temp.CreateDirectory("home", "Documents");
        Directory.CreateSymbolicLink(Path.Combine(profile, "Documents"), documents);
        Directory.CreateSymbolicLink(Path.Combine(profile, "Desktop"), temp.GetPath("missing"));

        var plan = WinePrefixService.Inspect(root, nested, WinePrefixRuntime.Proton);
        var lines = WinePrefixService.Describe(plan);

        Assert.Equal(PrefixLayout.RootAlias, plan.Layout);
        Assert.False(plan.HasSeparateTrees);
        Assert.Equal(root, plan.ResolvedWindowsPrefix);
        Assert.Contains($"  steamuser/Documents → {documents}", lines);
        Assert.Contains($"  someone/Desktop → {temp.GetPath("missing")} [!]", lines);
        Assert.Empty(Directory.EnumerateFileSystemEntries(documents));
    }

    [Fact]
    public void Prepare_CreatesPortableWinePrefixAndDriveMappings()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("My  Game") { Id = "item-id" };
        var startInfo = new ProcessStartInfo();
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Wine);

        var relativePrefix = Path.Combine("Prefixes", "item-id_My_Game");
        var prefixPath = Path.Combine(libraryRoot, relativePrefix);
        var dosDevicesPath = Path.Combine(prefixPath, "dosdevices");
        Assert.Equal(relativePrefix, item.PrefixPath);
        Assert.Equal(prefixPath, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("STEAM_COMPAT_DATA_PATH"));
        Assert.True(Directory.Exists(Path.Combine(prefixPath, "drive_c")));
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Combine(dosDevicesPath, "c:")).LinkTarget);
        AssertMappingTargets(Path.Combine(dosDevicesPath, "r:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void Prepare_CreatesNestedPfxForNewProtonPrefix()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("Proton Game") { Id = "proton-id" };
        var startInfo = new ProcessStartInfo();
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Proton);

        var prefixRoot = temp.GetPath("Library", "Prefixes", "proton-id_Proton_Game");
        var pfxPath = Path.Combine(prefixRoot, "pfx");
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(pfxPath, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.True(Directory.Exists(Path.Combine(pfxPath, "drive_c")));
        Assert.True(Directory.Exists(Path.Combine(pfxPath, "dosdevices", "c:")));
    }

    [Fact]
    public void Prepare_LeavesNewUmuPfxForUmuRunToCreate()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("UMU Game") { Id = "umu-id" };
        var startInfo = new ProcessStartInfo();
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Umu);

        var prefixRoot = temp.GetPath("Library", "Prefixes", "umu-id_UMU_Game");
        var dosDevicesPath = Path.Combine(prefixRoot, "dosdevices");
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.False(Directory.Exists(Path.Combine(prefixRoot, "pfx")));
        Assert.False(Directory.Exists(Path.Combine(prefixRoot, "drive_c")));
        Assert.False(Directory.Exists(Path.Combine(dosDevicesPath, "c:")));
        AssertMappingTargets(Path.Combine(dosDevicesPath, "r:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void Prepare_PreservesLegacyRootPrefixForProton()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.CreateDirectory("Library", "Prefixes", "Legacy");
        temp.CreateDirectory("Library", "Prefixes", "Legacy", "drive_c");
        var item = new MediaItem("Legacy")
        {
            PrefixPath = Path.Combine("Prefixes", "Legacy")
        };
        var startInfo = new ProcessStartInfo();
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Proton);

        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.False(Directory.Exists(Path.Combine(prefixRoot, "pfx")));
    }

    [Fact]
    public void Prepare_UsesParentAsCompatRootWhenStoredProtonPathEndsInPfx()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.CreateDirectory("Library", "Prefixes", "Existing");
        var pfxPath = temp.CreateDirectory("Library", "Prefixes", "Existing", "pfx");
        temp.CreateDirectory("Library", "Prefixes", "Existing", "pfx", "drive_c");
        var item = new MediaItem("Existing")
        {
            PrefixPath = Path.Combine("Prefixes", "Existing", "pfx")
        };
        var startInfo = new ProcessStartInfo();
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Proton);

        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(pfxPath, startInfo.EnvironmentVariables["WINEPREFIX"]);
    }

    [Fact]
    public void Prepare_ConvertsExistingInternalAbsolutePrefixToPortablePathWhenEnabled()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = temp.CreateDirectory("Library", "Prefixes", "Existing");
        var item = new MediaItem("Existing")
        {
            PrefixPath = prefixPath
        };
        var settings = new AppSettings { PreferPortableLaunchPaths = true };
        var service = new WinePrefixService(libraryRoot, settings);

        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);

        Assert.Equal(Path.Combine("Prefixes", "Existing"), item.PrefixPath);
    }

    [Fact]
    public void Prepare_DoesNotCreateLibraryMappingForExternalPrefix()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var externalPrefix = temp.GetPath("External", "Prefix");
        var item = new MediaItem("External")
        {
            PrefixPath = externalPrefix
        };
        var settings = new AppSettings { PreferPortableLaunchPaths = true };
        var service = new WinePrefixService(libraryRoot, settings);

        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);

        Assert.Equal(externalPrefix, item.PrefixPath);
        Assert.False(Directory.Exists(Path.Combine(externalPrefix, "dosdevices", "d:")));
        Assert.False(Directory.Exists(Path.Combine(externalPrefix, "dosdevices", "r:")));
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Games")));
    }

    [Fact]
    public void Prepare_OnLinuxDoesNotTreatDifferentlyCasedSiblingAsInternalPrefix()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var externalPrefix = temp.GetPath("library", "Prefixes", "External");
        var item = new MediaItem("External")
        {
            PrefixPath = externalPrefix
        };
        var service = new WinePrefixService(
            libraryRoot,
            new AppSettings { PreferPortableLaunchPaths = true });

        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);

        Assert.Equal(externalPrefix, item.PrefixPath);
        Assert.False(Directory.Exists(Path.Combine(externalPrefix, "dosdevices", "d:")));
        Assert.False(Directory.Exists(Path.Combine(externalPrefix, "dosdevices", "r:")));
        Assert.False(Directory.Exists(Path.Combine(libraryRoot, "Games")));
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
