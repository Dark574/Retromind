using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class SystemBrowserLauncherTests
{
    [Fact]
    public void CreateStartInfo_OnLinuxUsesSanitizedXdgOpen()
    {
        using var environment = new EnvironmentVariableScope(
            ("LD_LIBRARY_PATH", "/tmp/retromind-appimage-libs"),
            ("VLC_PLUGIN_PATH", "/tmp/retromind-vlc-plugins"));
        var uri = new Uri("https://example.com/login?state=test");

        var startInfo = SystemBrowserLauncher.CreateStartInfo(uri);

        Assert.Equal("xdg-open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal([uri.AbsoluteUri], startInfo.ArgumentList);
        Assert.False(startInfo.Environment.ContainsKey("LD_LIBRARY_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("VLC_PLUGIN_PATH"));
    }

    [Fact]
    public void CreateStartInfo_RejectsRelativeUri()
    {
        var uri = new Uri("login", UriKind.Relative);

        Assert.Throws<ArgumentException>(() => SystemBrowserLauncher.CreateStartInfo(uri));
    }
}
