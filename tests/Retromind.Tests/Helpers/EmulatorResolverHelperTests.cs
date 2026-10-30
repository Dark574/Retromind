using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class EmulatorResolverHelperTests
{
    [Fact]
    public void BuildWineInstallStartInfo_SanitizesHostEnvironmentAndKeepsExplicitPrefix()
    {
        using var temp = new TemporaryDirectory();
        var portableHome = temp.GetPath("Home");
        var appDir = temp.GetPath("appimage");
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", appDir),
            ("HOME", portableHome), ("XDG_CONFIG_HOME", Path.Combine(portableHome, "config")),
            ("LD_LIBRARY_PATH", temp.GetPath("bundled-libs")), ("VLC_PLUGIN_PATH", temp.GetPath("vlc")),
            ("PATH", appDir + "/usr/bin:/usr/bin:/bin"), ("WINEPREFIX", "/unrelated-prefix"));
        var prefix = temp.GetPath("Library", "Prefixes", "test");

        var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo("/usr/bin/wine", prefix);

        Assert.Equal(prefix, startInfo.Environment["WINEPREFIX"]);
        Assert.Equal("-all", startInfo.Environment["WINEDEBUG"]);
        Assert.False(startInfo.Environment.ContainsKey("LD_LIBRARY_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("VLC_PLUGIN_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("XDG_CONFIG_HOME"));
        startInfo.Environment.TryGetValue("HOME", out var configuredHome);
        Assert.NotEqual(portableHome, configuredHome);
        Assert.Equal("/usr/bin:/bin", startInfo.Environment["PATH"]);
    }
}
