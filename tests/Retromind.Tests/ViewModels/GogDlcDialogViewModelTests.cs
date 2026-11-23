using System.Net;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class GogDlcDialogViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallSelected_UsesTheCurrentKeepOfflineInstallersChoice(bool keepInstallers)
    {
        using var client = new HttpClient(new DlcCatalogHandler());
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(new SecretKey("retromind:gog", "default"), "test-refresh");
        var auth = new GogAuthService(secrets, new GogOAuthClient(client), new GogPkceService(), new RecordingErrorService());
        var installService = new GogInstallService(auth, client, client);
        bool? submittedChoice = null;
        using var dialog = new GogDlcDialogViewModel(
            installService,
            "123",
            GogInstallPlatform.Linux,
            installAsync: (_, keep, _) =>
            {
                submittedChoice = keep;
                return Task.FromResult(new GogDlcInstallBatchResult(new HashSet<string> { "456" }, string.Empty));
            },
            keepOfflineInstallers: !keepInstallers);

        await dialog.LoadAsync();
        Assert.Equal(!keepInstallers, dialog.KeepOfflineInstallers);
        dialog.KeepOfflineInstallers = keepInstallers;
        dialog.SelectAllFilteredCommand.Execute(null);
        Assert.Equal(1, dialog.SelectedCount);
        await dialog.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(keepInstallers, submittedChoice);
    }

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

    private sealed class DlcCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/token" => """{"access_token":"test-access","refresh_token":"test-refresh","user_id":"test-user","expires_in":3600}""",
                "/products/123" => """
                    {"expanded_dlcs":[{"id":456,"title":"Test DLC","downloads":{"installers":[
                        {"os":"linux","files":[{"downlink":"https://api.gog.com/installer"}]}
                    ]}}]}
                    """,
                "/user/data/games" => """{"owned":[456]}""",
                _ => throw new InvalidOperationException("Unexpected GOG endpoint.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
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
