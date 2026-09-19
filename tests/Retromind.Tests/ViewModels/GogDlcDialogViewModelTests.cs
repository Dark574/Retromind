using Retromind.Services.Stores.Gog;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class GogDlcDialogViewModelTests
{
    [Fact]
    public void CatalogEntry_IsSelectableOnlyForInstalledPlatform()
    {
        var entry = CreateEntry(
            [GogInstallPlatform.Linux],
            GogInstallPlatform.Windows,
            isInstalled: false);

        Assert.False(entry.IsInstallableForCurrentPlatform);
        Assert.False(entry.IsSelectionEnabled);
    }

    [Fact]
    public void CatalogEntry_BecomesUnavailableAndClearsSelectionWhenInstalled()
    {
        var entry = CreateEntry(
            [GogInstallPlatform.Windows],
            GogInstallPlatform.Windows,
            isInstalled: false);
        entry.IsSelected = true;

        entry.MarkInstalledAsCurrent();

        Assert.False(entry.IsSelected);
        Assert.False(entry.IsSelectionEnabled);
        Assert.Equal("Up to date", entry.StatusText);
    }

    [Fact]
    public void CatalogEntry_AvailableUpdateCanBeSelected()
    {
        var installedState = new Retromind.Models.GogDlcInstallationState
        {
            ProductId = "123",
            InstalledVersion = "1.0",
            InstalledInstallerSignature = "legacy-signature"
        };
        var catalogItem = new GogDlcCatalogItem(
            "123",
            "Test DLC",
            [GogInstallPlatform.Windows],
            [new GogDlcInstallerMetadata(GogInstallPlatform.Windows, "1.1", "catalog-v1:remote")]);

        var entry = new GogDlcCatalogEntry(
            catalogItem,
            "Linux",
            "Windows",
            "No installer",
            "Up to date",
            "Update available",
            "Installed · update status unknown",
            GogInstallPlatform.Windows,
            isInstalled: true,
            installedState: installedState);

        Assert.True(entry.HasUpdateAvailable);
        Assert.Equal("Update available", entry.StatusText);
        Assert.True(entry.IsSelectionEnabled);

        entry.IsSelected = true;
        entry.MarkInstalledAsCurrent();

        Assert.False(entry.HasUpdateAvailable);
        Assert.False(entry.IsSelectionEnabled);
        Assert.False(entry.IsSelected);
        Assert.Equal("Up to date", entry.StatusText);
    }

    private static GogDlcCatalogEntry CreateEntry(
        IReadOnlyList<GogInstallPlatform> availablePlatforms,
        GogInstallPlatform? installedPlatform,
        bool isInstalled)
    {
        return new GogDlcCatalogEntry(
            new GogDlcCatalogItem("123", "Test DLC", availablePlatforms),
            "Linux",
            "Windows",
            "No installer",
            "Up to date",
            "Update available",
            "Installed · update status unknown",
            installedPlatform,
            isInstalled,
            installedState: null);
    }
}
