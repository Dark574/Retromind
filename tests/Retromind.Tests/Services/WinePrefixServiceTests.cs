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
        var pfx = Path.Combine(prefixPath, "pfx");
        var dosDevicesPath = Path.Combine(pfx, "dosdevices");
        Assert.Equal(relativePrefix, item.PrefixPath);
        Assert.Equal(pfx, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.Equal("steamuser", startInfo.Environment["USER"]);
        Assert.False(startInfo.EnvironmentVariables.ContainsKey("STEAM_COMPAT_DATA_PATH"));
        Assert.False(Directory.Exists(Path.Combine(prefixPath, "drive_c")));
        AssertPortableProfile(pfx);
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Combine(dosDevicesPath, "c:")).LinkTarget);
        Assert.Equal("/", new DirectoryInfo(Path.Combine(dosDevicesPath, "z:")).LinkTarget);
        AssertMappingTargets(Path.Combine(dosDevicesPath, "r:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void Prepare_CreatesNestedPfxForNewProtonPrefix()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("Proton Game") { Id = "proton-id" };
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["USER"] = "linux-user";
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Proton);

        var prefixRoot = temp.GetPath("Library", "Prefixes", "proton-id_Proton_Game");
        var pfxPath = Path.Combine(prefixRoot, "pfx");
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(pfxPath, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.Equal("linux-user", startInfo.Environment["USER"]);
        Assert.True(Directory.Exists(Path.Combine(pfxPath, "drive_c")));
        Assert.True(Directory.Exists(Path.Combine(pfxPath, "dosdevices", "c:")));
    }

    [Fact]
    public void Prepare_GivesUmuItsCompatibilityRootAndAnExistingRealPortablePfx()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("UMU Game") { Id = "umu-id" };
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["USER"] = "linux-user";
        var service = new WinePrefixService(libraryRoot, new AppSettings());

        service.Prepare(item, startInfo, WinePrefixRuntime.Umu);

        var prefixRoot = temp.GetPath("Library", "Prefixes", "umu-id_UMU_Game");
        var pfx = Path.Combine(prefixRoot, "pfx");
        var dosDevicesPath = Path.Combine(pfx, "dosdevices");
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(prefixRoot, startInfo.EnvironmentVariables["WINEPREFIX"]);
        Assert.Equal("linux-user", startInfo.Environment["USER"]);
        Assert.Null(new DirectoryInfo(pfx).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(prefixRoot, "drive_c")));
        Assert.False(Directory.Exists(Path.Combine(prefixRoot, "dosdevices")));
        AssertPortableProfile(pfx);
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Combine(dosDevicesPath, "c:")).LinkTarget);
        Assert.Equal("/", new DirectoryInfo(Path.Combine(dosDevicesPath, "z:")).LinkTarget);
        AssertMappingTargets(Path.Combine(dosDevicesPath, "r:"), Path.Combine(libraryRoot, "Games"));
    }

    [Fact]
    public void Preview_PlansPortableProfileWithoutCreatingThePrefix()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var relative = Path.Combine("Prefixes", "Game");
        var item = new MediaItem("Game") { PrefixPath = relative };
        var start = new ProcessStartInfo();
        var service = new WinePrefixService(library, new AppSettings());

        var preview = service.PrepareLaunch(item, start, WinePrefixRuntime.Wine, true, scaffold: false);

        Assert.NotNull(preview);
        Assert.True(preview.PortableProfile);
        Assert.Equal(Path.Combine(library, relative, "pfx"), start.Environment["WINEPREFIX"]);
        Assert.Equal("steamuser", start.Environment["USER"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(library));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("alias")]
    public void Prepare_PreservesExistingUnmarkedProfiles(string layout)
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var root = temp.CreateDirectory("Library", "Prefixes", "Legacy");
        var tree = layout == "nested" ? Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName : root;
        if (layout == "alias")
            Directory.CreateSymbolicLink(Path.Combine(root, "pfx"), ".");
        var user = Directory.CreateDirectory(Path.Combine(tree, "drive_c", "users", "steamuser")).FullName;
        var external = temp.CreateDirectory("Home", "Documents");
        var documents = Path.Combine(user, "Documents");
        Directory.CreateSymbolicLink(documents, external);
        var item = new MediaItem("Legacy") { PrefixPath = root };
        var start = new ProcessStartInfo();
        start.Environment["USER"] = "linux-user";

        var plan = new WinePrefixService(library, new AppSettings())
            .PrepareLaunch(item, start, WinePrefixRuntime.Wine, true, scaffold: true);

        Assert.NotNull(plan);
        Assert.False(plan.PortableProfile);
        Assert.Equal(tree, start.Environment["WINEPREFIX"]);
        Assert.Equal("linux-user", start.Environment["USER"]);
        Assert.Equal(external, new DirectoryInfo(documents).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(user, "Pictures")));
    }

    [Fact]
    public void Prepare_ReportsProfileConflictWithoutFollowingOrReplacingProfileLink()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var item = new MediaItem("Game") { PrefixPath = Path.Combine("Prefixes", "Game") };
        var service = new WinePrefixService(library, new AppSettings());
        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);
        var documents = temp.GetPath("Library", "Prefixes", "Game", "pfx", "drive_c", "users", "steamuser", "Documents");
        Directory.Delete(documents);
        var external = temp.CreateDirectory("external");
        Directory.CreateSymbolicLink(documents, external);

        var start = new ProcessStartInfo();
        var plan = service.PrepareLaunch(item, start, WinePrefixRuntime.Umu, true, scaffold: true);

        Assert.NotNull(plan);
        Assert.True(plan.ProfileConflict);
        Assert.False(plan.CanPrepare);
        Assert.Contains(WinePrefixService.PortableProfileConflictMessage, WinePrefixService.Describe(plan));
        Assert.Equal(external, new DirectoryInfo(documents).LinkTarget);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
    }

    [Fact]
    public void Inspect_RecognizesAnExplicitWineAliasButRejectsUsingTheV2TreeAsUmuCompatRoot()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var item = new MediaItem("Game") { PrefixPath = Path.Combine("Prefixes", "Game") };
        new WinePrefixService(library, new AppSettings()).Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);
        var pfx = temp.GetPath("Library", "Prefixes", "Game", "pfx");
        var alias = temp.GetPath("wine-prefix-alias");
        Directory.CreateSymbolicLink(alias, pfx);

        var wine = WinePrefixService.ResolveExplicitOverride(WinePrefixRuntime.Wine,
            new Dictionary<string, string> { ["WINEPREFIX"] = alias }, out _);
        var umu = WinePrefixService.ResolveExplicitOverride(WinePrefixRuntime.Umu,
            new Dictionary<string, string> { ["WINEPREFIX"] = pfx }, out _);

        Assert.NotNull(wine);
        Assert.True(wine.PortableProfile && wine.CanPrepare);
        Assert.Equal(pfx, wine.ResolvedWindowsPrefix);
        Assert.NotNull(umu);
        Assert.True(umu.ProfileConflict);
        Assert.False(umu.CanPrepare);
        Assert.False(Directory.Exists(Path.Combine(pfx, "pfx")));
    }

    [Fact]
    public void PreparePortableProfile_DoesNotAdoptALegacyPrefixCreatedAfterPlanning()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var root = temp.GetPath("Library", "Prefixes", "Game");
        var planned = WinePrefixService.ResolveManaged(root, WinePrefixRuntime.Wine, library);
        var save = temp.CreateFile("Library/Prefixes/Game/drive_c/save.dat", "existing");

        Assert.Throws<InvalidOperationException>(() => WinePrefixService.PreparePortableProfile(planned, library));
        Assert.False(Directory.Exists(Path.Combine(root, "pfx")));
        Assert.Equal("existing", File.ReadAllText(save));
    }

    [Fact]
    public void PreparePortableProfile_RejectsPrefixParentRedirectedAfterPlanning()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var parent = Path.Combine(library, "Prefixes");
        var planned = WinePrefixService.ResolveManaged(Path.Combine(parent, "Game"), WinePrefixRuntime.Wine, library);
        var external = temp.CreateDirectory("External");
        Directory.CreateSymbolicLink(parent, external);

        Assert.Throws<InvalidOperationException>(() => WinePrefixService.PreparePortableProfile(planned, library));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
    }

    [Fact]
    public void Prepare_ReusesFallbackGamesDriveAcrossRunnersAndLibraryMove()
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var item = new MediaItem("Game") { PrefixPath = Path.Combine("Prefixes", "Game") };
        var service = new WinePrefixService(library, new AppSettings());
        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);
        var pfx = Path.Combine(library, item.PrefixPath, "pfx");
        var drives = Path.Combine(pfx, "dosdevices");
        var userDrive = Directory.CreateDirectory(Path.Combine(library, "User Drive")).FullName;
        var occupied = Path.Combine(drives, "r:");
        File.Delete(occupied);
        var userTarget = Path.GetRelativePath(drives, userDrive);
        Directory.CreateSymbolicLink(occupied, userTarget);
        service.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Proton);
        var qTarget = new DirectoryInfo(Path.Combine(drives, "q:")).LinkTarget;
        AssertMappingTargets(Path.Combine(drives, "q:"), Path.Combine(library, "Games"));

        var moved = temp.GetPath("Moved Library");
        Directory.Move(library, moved);
        var newService = new WinePrefixService(moved, new AppSettings());
        newService.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Umu);
        newService.Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);

        var movedDrives = Path.Combine(moved, item.PrefixPath, "pfx", "dosdevices");
        Assert.Equal(userTarget, new DirectoryInfo(Path.Combine(movedDrives, "r:")).LinkTarget);
        Assert.Equal(qTarget, new DirectoryInfo(Path.Combine(movedDrives, "q:")).LinkTarget);
        AssertMappingTargets(Path.Combine(movedDrives, "q:"), Path.Combine(moved, "Games"));
        Assert.True(Directory.Exists(Path.Combine(movedDrives, "c:")));
        Assert.True(Directory.Exists(Path.Combine(movedDrives, "z:")));
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
        Assert.False(Path.IsPathRooted(linkTarget));
        Assert.Equal(
            Path.GetFullPath(expectedTarget),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mappingPath)!, linkTarget)));
    }

    private static void AssertPortableProfile(string pfx)
    {
        var profile = Path.Combine(pfx, "drive_c", "users", "steamuser");
        foreach (var folder in new[] { "Documents", "Desktop", "Downloads", "Saved Games", "Pictures", "Music",
                     "Videos", "AppData", "AppData/Roaming", "AppData/Local", "AppData/LocalLow" })
        {
            var directory = new DirectoryInfo(Path.Combine(profile, folder));
            Assert.True(directory.Exists);
            Assert.Null(directory.LinkTarget);
        }
    }
}
