using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class GogMediaItemStateHelperTests
{
    [Fact]
    public void CanCheckUpdates_RequiresManagedInstallPath()
    {
        using var temp = new TemporaryDirectory();
        var executable = temp.CreateFile("game.exe", "binary");
        var item = CreateGogItem();
        item.Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = executable,
                Index = 1
            }
        ];

        Assert.True(GogMediaItemStateHelper.IsInstalled(item));
        Assert.False(GogMediaItemStateHelper.CanCheckUpdates(item));

        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = temp.RootPath;

        Assert.True(GogMediaItemStateHelper.CanCheckUpdates(item));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  build 42  ", "build 42")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void TryGetInstalledVersion_ReturnsOnlyKnownVersion(string storedVersion, string? expected)
    {
        var item = CreateGogItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = storedVersion;

        Assert.Equal(expected, GogMediaItemStateHelper.TryGetInstalledVersion(item));
    }

    [Fact]
    public void InstallState_RequiresGogIdentityAndPlayableLaunchConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var executablePath = temp.CreateFile("game.sh");
        var item = CreateGogItem();

        Assert.True(GogMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.False(GogMediaItemStateHelper.IsInstalled(item));

        item.LauncherPath = executablePath;

        Assert.False(GogMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.True(GogMediaItemStateHelper.IsInstalled(item));
    }

    [Fact]
    public void CanUninstall_RequiresStoredInstallPathRatherThanPlayableLauncher()
    {
        var item = CreateGogItem();
        item.LauncherPath = "external-launch-command";

        Assert.True(GogMediaItemStateHelper.IsInstalled(item));
        Assert.False(GogMediaItemStateHelper.CanUninstall(item));

        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = "Games/GOG/Test";

        Assert.True(GogMediaItemStateHelper.CanUninstall(item));

        item.LauncherPath = null;

        Assert.False(GogMediaItemStateHelper.IsInstalled(item));
        Assert.True(GogMediaItemStateHelper.CanUninstall(item));
    }

    [Fact]
    public void HasUpdateAvailable_RequiresInstalledGogItem()
    {
        var item = CreateGogItem();
        item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "true";

        Assert.False(GogMediaItemStateHelper.HasUpdateAvailable(item));

        item.LauncherPath = "external-launch-command";

        Assert.True(GogMediaItemStateHelper.HasUpdateAvailable(item));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void IsTruthyCustomField_RecognizesSupportedValues(string raw, bool expected)
    {
        Assert.Equal(expected, GogMediaItemStateHelper.IsTruthyCustomField(raw));
    }

    private static MediaItem CreateGogItem()
    {
        var item = new MediaItem("GOG game");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
        return item;
    }
}
