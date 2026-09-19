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

        entry.IsInstalled = true;

        Assert.False(entry.IsSelected);
        Assert.False(entry.IsSelectionEnabled);
        Assert.Equal("Installed", entry.StatusText);
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
            "Installed",
            installedPlatform,
            isInstalled);
    }
}
