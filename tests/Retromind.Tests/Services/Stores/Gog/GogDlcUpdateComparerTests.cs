using Retromind.Models;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogDlcUpdateComparerTests
{
    [Fact]
    public void Evaluate_DifferentVersion_ReturnsUpdateAvailable()
    {
        var installed = CreateInstalled("1.0", "legacy-signature");
        var remote = CreateRemote("1.1", "catalog-v1:remote");

        Assert.Equal(GogDlcUpdateState.UpdateAvailable, GogDlcUpdateComparer.Evaluate(installed, remote));
    }

    [Fact]
    public void Evaluate_SameVersionAndLegacySignature_ReturnsUpToDate()
    {
        var installed = CreateInstalled("1.0", "legacy-signature");
        var remote = CreateRemote("1.0", "catalog-v1:remote");

        Assert.Equal(GogDlcUpdateState.UpToDate, GogDlcUpdateComparer.Evaluate(installed, remote));
    }

    [Fact]
    public void Evaluate_SameVersionButChangedCatalogSignature_ReturnsUpdateAvailable()
    {
        var installed = CreateInstalled("1.0", "catalog-v1:installed");
        var remote = CreateRemote("1.0", "catalog-v1:remote");

        Assert.Equal(GogDlcUpdateState.UpdateAvailable, GogDlcUpdateComparer.Evaluate(installed, remote));
    }

    [Fact]
    public void HasUpdate_OnlyChecksInstalledDlcs()
    {
        var installed = new[] { CreateInstalled("1.0", "catalog-v1:same") };
        var catalog = new[]
        {
            new GogDlcCatalogItem(
                "123",
                "Installed DLC",
                [GogInstallPlatform.Windows],
                [CreateRemote("1.0", "catalog-v1:same")]),
            new GogDlcCatalogItem(
                "456",
                "Uninstalled DLC",
                [GogInstallPlatform.Windows],
                [CreateRemote("2.0", "catalog-v1:different")])
        };

        Assert.False(GogDlcUpdateComparer.HasUpdate(installed, catalog, GogInstallPlatform.Windows));
    }

    [Fact]
    public void HasUpdate_ReturnsTrueForChangedInstalledDlcOnCurrentPlatform()
    {
        var installed = new[] { CreateInstalled("1.0", "catalog-v1:installed") };
        var catalog = new[]
        {
            new GogDlcCatalogItem(
                "123",
                "Installed DLC",
                [GogInstallPlatform.Windows],
                [CreateRemote("1.1", "catalog-v1:remote")])
        };

        Assert.True(GogDlcUpdateComparer.HasUpdate(installed, catalog, GogInstallPlatform.Windows));
        Assert.False(GogDlcUpdateComparer.HasUpdate(installed, catalog, GogInstallPlatform.Linux));
    }

    private static GogDlcInstallationState CreateInstalled(string version, string signature) => new()
    {
        ProductId = "123",
        Title = "DLC",
        Platform = "windows",
        InstalledVersion = version,
        InstalledInstallerSignature = signature
    };

    private static GogDlcInstallerMetadata CreateRemote(string version, string signature) =>
        new(GogInstallPlatform.Windows, version, signature);
}
