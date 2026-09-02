using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallServicePortabilityTests
{
    [Fact]
    public async Task Uninstall_RelativeInstallPathAfterMove_DeletesOnlyCurrentPortableRoot()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();
        var relativeInstallPath = Path.Combine("Library", "Games", "GOG", "Portable Game");
        var item = CreateInstalledItem(relativeInstallPath);
        var firstInstallPath = firstRoot.CreateDirectory("Library", "Games", "GOG", "Portable Game");
        var secondInstallPath = secondRoot.CreateDirectory("Library", "Games", "GOG", "Portable Game");
        GogInstallDirectorySafety.WriteMarker(firstInstallPath, item);
        GogInstallDirectorySafety.WriteMarker(secondInstallPath, item);
        var oldRootSentinel = firstRoot.CreateFile(
            Path.Combine("Library", "Games", "GOG", "Portable Game", "old-root.txt"),
            "must remain");
        secondRoot.CreateFile(
            Path.Combine("Library", "Games", "GOG", "Portable Game", "current-root.txt"));

        using (UseDataRoot(secondRoot.RootPath))
        {
            var service = CreateInstallService();

            await service.UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(secondInstallPath));
        Assert.True(File.Exists(oldRootSentinel));
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreInstallPath));
    }

    private static MediaItem CreateInstalledItem(string storedInstallPath)
    {
        var item = new MediaItem
        {
            Id = "portable-item",
            Title = "Portable Game"
        };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "portable-game-id";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = storedInstallPath;
        return item;
    }

    private static GogInstallService CreateInstallService()
    {
        var oauthHttpClient = new HttpClient();
        var providerHttpClient = new HttpClient();
        var authService = new GogAuthService(
            new InMemorySecretStore(),
            new GogOAuthClient(oauthHttpClient),
            new GogPkceService());
        return new GogInstallService(authService, providerHttpClient);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
