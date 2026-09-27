using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class WinetricksServiceTests
{
    [Fact]
    public void PreparePrefix_CreatesWineDirectoriesAndPortableDriveMappings()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var service = new WinetricksService(libraryRoot);

        service.PreparePrefix(prefixRoot, isProton: false, isUmu: false);

        var dosDevices = Path.Combine(prefixRoot, "dosdevices");
        Assert.True(Directory.Exists(Path.Combine(prefixRoot, "drive_c")));
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Combine(dosDevices, "c:")).LinkTarget);
        Assert.NotNull(new DirectoryInfo(Path.Combine(dosDevices, "d:")).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(libraryRoot, "Games")));
    }

    [Fact]
    public void PrepareExecution_UsesUmuAndPreservesQuotedVerbArguments()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var protonRoot = temp.CreateDirectory("GE-Proton");
        temp.CreateFile("GE-Proton/protonfixes/winetricks");
        var service = new WinetricksService(libraryRoot);

        var plan = service.PrepareExecution(new WinetricksRequest(
            prefixRoot,
            "vcrun2022 \"dxvk hud\"",
            new Dictionary<string, string> { ["PROTONPATH"] = protonRoot },
            IsProton: true,
            IsUmu: true,
            UmuRunnerPath: "/usr/bin/umu-run"));

        Assert.True(plan.UsesUmu);
        Assert.Equal("/usr/bin/umu-run", plan.FileName);
        Assert.Equal(["winetricks", "vcrun2022", "dxvk hud"], plan.Arguments);
        Assert.Equal(prefixRoot, plan.CompatRoot);
        Assert.Equal(prefixRoot, plan.WinePrefix);
        Assert.Equal(prefixRoot, plan.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(prefixRoot, plan.Environment["WINEPREFIX"]);
        Assert.Null(plan.MissingBundledWinetricksPath);
    }

    [Fact]
    public void PrepareExecution_FallsBackToSystemWinetricksWithProtonWineBinaries()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var protonRoot = temp.CreateDirectory("GE-Proton");
        var wine = temp.CreateFile("GE-Proton/files/bin/wine");
        var wineserver = temp.CreateFile("GE-Proton/files/bin/wineserver");
        var wine64 = temp.CreateFile("GE-Proton/files/bin/wine64");
        var service = new WinetricksService(libraryRoot);

        var plan = service.PrepareExecution(new WinetricksRequest(
            prefixRoot,
            "corefonts",
            new Dictionary<string, string> { ["PROTONPATH"] = protonRoot },
            IsProton: true,
            IsUmu: true,
            UmuRunnerPath: "/usr/bin/umu-run"));

        Assert.False(plan.UsesUmu);
        Assert.Equal("winetricks", plan.FileName);
        Assert.Equal(["corefonts"], plan.Arguments);
        Assert.Equal(Path.Combine(prefixRoot, "pfx"), plan.WinePrefix);
        Assert.Equal(wine, plan.Environment["WINE"]);
        Assert.Equal(wineserver, plan.Environment["WINESERVER"]);
        Assert.Equal(wine64, plan.Environment["WINE64"]);
        Assert.StartsWith(Path.GetDirectoryName(wine), plan.Environment["PATH"], StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(protonRoot, "protonfixes", "winetricks"),
            plan.MissingBundledWinetricksPath);
    }

    [Fact]
    public void ResolvePrefixPaths_UsesExistingRootStyleProtonPrefix()
    {
        using var temp = new TemporaryDirectory();
        var prefixRoot = temp.CreateDirectory("prefix");
        temp.CreateFile("prefix/system.reg");

        var paths = WinetricksService.ResolvePrefixPaths(
            prefixRoot,
            isProton: true,
            isUmu: false);

        Assert.Equal(prefixRoot, paths.CompatRoot);
        Assert.Equal(prefixRoot, paths.WinePrefix);
    }

    [Fact]
    public async Task Run_PreparationFailureIsReportedWithoutStartingAProcess()
    {
        using var temp = new TemporaryDirectory();
        var service = new WinetricksService(temp.CreateDirectory("Library"));
        var log = new List<string>();

        await service.RunAsync(
            new WinetricksRequest(
                PrefixRoot: string.Empty,
                Verbs: "corefonts",
                EnvironmentOverrides: new Dictionary<string, string>(),
                IsProton: false,
                IsUmu: false,
                UmuRunnerPath: "umu-run"),
            log.Add);

        var message = Assert.Single(log);
        Assert.StartsWith("Error while preparing Winetricks:", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vcrun2022 corefonts", "vcrun2022", "corefonts")]
    [InlineData("'dot net' \"dxvk hud\"", "dot net", "dxvk hud")]
    public void SplitArguments_PreservesQuotedGroups(
        string input,
        string first,
        string second)
    {
        var arguments = WinetricksService.SplitArguments(input);

        Assert.Equal([first, second], arguments);
    }
}
