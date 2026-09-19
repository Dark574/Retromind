using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogLinuxInstallRelocationRepairTests
{
    [Fact]
    public void RepairFromManifest_RewritesMovedMojoSetupPaths()
    {
        using var temp = new TemporaryDirectory();
        var installRoot = temp.CreateDirectory("Library", "Games", "PC Games", "Test Game");
        var manifestDirectory = Directory.CreateDirectory(
            Path.Combine(installRoot, ".mojosetup", "manifest")).FullName;
        var oldRoot = "/tmp/retromind-gog-install/game/install-old";
        var xmlPath = Path.Combine(manifestDirectory, "Test.xml");
        var luaPath = Path.Combine(manifestDirectory, "Test.lua");
        var desktopPath = Path.Combine(installRoot, ".mojosetup", "Test.desktop");
        File.WriteAllText(xmlPath, $"<product name=\"Test\" root=\"{oldRoot}\" />");
        File.WriteAllText(luaPath, $"return {{ root = \"{oldRoot}\" }}");
        File.WriteAllText(desktopPath, $"Exec=\"{oldRoot}/start.sh\"");

        var changed = GogLinuxInstallRelocationRepair.RepairFromManifest(installRoot);

        Assert.Equal(3, changed);
        Assert.Contains(installRoot, File.ReadAllText(xmlPath), StringComparison.Ordinal);
        Assert.Contains(installRoot, File.ReadAllText(luaPath), StringComparison.Ordinal);
        Assert.Contains(installRoot, File.ReadAllText(desktopPath), StringComparison.Ordinal);
        Assert.DoesNotContain(oldRoot, File.ReadAllText(xmlPath), StringComparison.Ordinal);
    }

    [Fact]
    public void RepairFromManifest_CurrentRootLeavesFilesUnchanged()
    {
        using var temp = new TemporaryDirectory();
        var installRoot = temp.CreateDirectory("installed-game");
        var manifestDirectory = Directory.CreateDirectory(
            Path.Combine(installRoot, ".mojosetup", "manifest")).FullName;
        var xmlPath = Path.Combine(manifestDirectory, "Test.xml");
        var original = $"<product name=\"Test\" root=\"{installRoot}\" />";
        File.WriteAllText(xmlPath, original);

        var changed = GogLinuxInstallRelocationRepair.RepairFromManifest(installRoot);

        Assert.Equal(0, changed);
        Assert.Equal(original, File.ReadAllText(xmlPath));
    }
}
