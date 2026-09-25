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
    public async Task Uninstall_RelativePrefixWithTrailingSeparators_DeletesCanonicalPrefix()
    {
        using var portableRoot = new TemporaryDirectory();
        var installPath = portableRoot.CreateDirectory("Library", "Games", "GOG", "Slash Game");
        var prefixPath = portableRoot.CreateDirectory("Library", "Prefixes", "Slash Game");
        var item = CreateInstalledItem(Path.Combine("Library", "Games", "GOG", "Slash Game"));
        item.PrefixPath = "Prefixes/Slash Game//";
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        portableRoot.CreateFile("Library/Prefixes/Slash Game/prefix-state.txt", "delete with prefix");

        using (UseDataRoot(portableRoot.RootPath))
        {
            await CreateInstallService().UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(installPath));
        Assert.False(Directory.Exists(prefixPath));
        Assert.Null(item.PrefixPath);
    }

    [Fact]
    public async Task Uninstall_ExternalPrefixWithTrailingSeparators_PreservesPrefixAndMetadata()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        var installPath = portableRoot.CreateDirectory("Library", "Games", "GOG", "External Prefix Game");
        var externalPrefix = externalRoot.CreateDirectory("prefix");
        var externalSentinel = externalRoot.CreateFile("prefix/must-remain.txt", "external prefix data");
        var item = CreateInstalledItem(
            Path.Combine("Library", "Games", "GOG", "External Prefix Game"));
        item.PrefixPath = externalPrefix + "//";
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        using (UseDataRoot(portableRoot.RootPath))
        {
            await CreateInstallService().UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(installPath));
        Assert.True(Directory.Exists(externalPrefix));
        Assert.True(File.Exists(externalSentinel));
        Assert.Equal(externalPrefix + "//", item.PrefixPath);
    }

    [Theory]
    [InlineData("Prefixes")]
    [InlineData("Games")]
    public async Task Uninstall_SharedLibraryRootWithTrailingSeparators_PreservesDirectory(
        string sharedDirectoryName)
    {
        using var portableRoot = new TemporaryDirectory();
        var installPath = portableRoot.CreateDirectory("Library", "Games", "GOG", "Shared Prefix Game");
        var sharedDirectory = portableRoot.CreateDirectory("Library", sharedDirectoryName);
        var otherPrefixSentinel = portableRoot.CreateFile(
            Path.Combine("Library", sharedDirectoryName, "Other Game", "must-remain.txt"),
            "other prefix data");
        var item = CreateInstalledItem(
            Path.Combine("Library", "Games", "GOG", "Shared Prefix Game"));
        item.PrefixPath = sharedDirectoryName + "//";
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        using (UseDataRoot(portableRoot.RootPath))
        {
            await CreateInstallService().UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(installPath));
        Assert.True(Directory.Exists(sharedDirectory));
        Assert.True(File.Exists(otherPrefixSentinel));
        Assert.Equal(sharedDirectoryName + "//", item.PrefixPath);
    }

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
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreDlcUpdateAvailable));
        Assert.Equal(MediaType.Native, item.MediaType);
        Assert.Null(item.EmulatorId);
        Assert.Null(item.LauncherPath);
        Assert.Null(item.LauncherArgs);
        Assert.Null(item.RunnerVersionId);
        Assert.Null(item.GogDlcInstallations);
    }

    [Fact]
    public async Task Uninstall_UnownedDirectory_PreservesDlcInstallationState()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("unowned-install");
        var sentinelPath = temp.CreateFile("unowned-install/game.bin", "keep");
        var item = CreateInstalledItem(installPath);
        var service = CreateInstallService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UninstallGogGameAsync(item));

        Assert.True(File.Exists(sentinelPath));
        Assert.Single(item.GogDlcInstallations!);
    }

    [Fact]
    public async Task Uninstall_AbsoluteOwnedDirectoryWithoutSymlinks_RemainsSupported()
    {
        using var externalRoot = new TemporaryDirectory();
        var installPath = externalRoot.CreateDirectory("absolute-install");
        var item = CreateInstalledItem(installPath);
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        externalRoot.CreateFile("absolute-install/game.bin", "installed game");

        await CreateInstallService().UninstallGogGameAsync(item);

        Assert.False(Directory.Exists(installPath));
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreInstallPath));
    }

    [Fact]
    public async Task Uninstall_AncestorDirectorySymlink_RefusesAndPreservesExternalInstall()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        var libraryRoot = portableRoot.CreateDirectory("Library");
        var externalInstall = externalRoot.CreateDirectory("game");
        var externalSentinel = externalRoot.CreateFile("game/must-remain.txt", "external data");
        var linkPath = Path.Combine(libraryRoot, "external-link");
        Directory.CreateSymbolicLink(linkPath, externalRoot.RootPath);
        var item = CreateInstalledItem(Path.Combine("Library", "external-link", "game"));
        GogInstallDirectorySafety.WriteMarker(externalInstall, item);

        using (UseDataRoot(portableRoot.RootPath))
        {
            var service = CreateInstallService();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UninstallGogGameAsync(item));
        }

        Assert.True(File.Exists(externalSentinel));
        Assert.True(Directory.Exists(externalInstall));
        Assert.True(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreInstallPath));
    }

    [Fact]
    public async Task Uninstall_NestedDirectorySymlink_DeletesOnlyLinkAndPreservesExternalTarget()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        var installPath = portableRoot.CreateDirectory("Library", "Games", "GOG", "Linked Game");
        var externalSentinel = externalRoot.CreateFile("must-remain.txt", "external data");
        var nestedLink = Path.Combine(installPath, "external-link");
        Directory.CreateSymbolicLink(nestedLink, externalRoot.RootPath);
        var item = CreateInstalledItem(Path.Combine("Library", "Games", "GOG", "Linked Game"));
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        using (UseDataRoot(portableRoot.RootPath))
        {
            await CreateInstallService().UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(installPath));
        Assert.True(File.Exists(externalSentinel));
        Assert.False(item.CustomFields.ContainsKey(CustomFieldKeyHelper.StoreInstallPath));
    }

    [Fact]
    public async Task Uninstall_PrefixAncestorSymlink_SkipsPrefixAndPreservesMetadata()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        var libraryRoot = portableRoot.CreateDirectory("Library");
        var installPath = portableRoot.CreateDirectory("Library", "Games", "GOG", "Prefix Game");
        var externalPrefix = externalRoot.CreateDirectory("prefix");
        var externalSentinel = externalRoot.CreateFile("prefix/must-remain.txt", "external prefix data");
        var linkPath = Path.Combine(libraryRoot, "prefix-link");
        Directory.CreateSymbolicLink(linkPath, externalRoot.RootPath);
        var item = CreateInstalledItem(Path.Combine("Library", "Games", "GOG", "Prefix Game"));
        item.PrefixPath = Path.Combine("prefix-link", "prefix");
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        using (UseDataRoot(portableRoot.RootPath))
        {
            await CreateInstallService().UninstallGogGameAsync(item);
        }

        Assert.False(Directory.Exists(installPath));
        Assert.True(Directory.Exists(externalPrefix));
        Assert.True(File.Exists(externalSentinel));
        Assert.Equal(Path.Combine("prefix-link", "prefix"), item.PrefixPath);
    }

    private static MediaItem CreateInstalledItem(string storedInstallPath)
    {
        var item = new MediaItem
        {
            Id = "portable-item",
            Title = "Portable Game",
            GogDlcInstallations =
            [
                new GogDlcInstallationState
                {
                    ProductId = "dlc-id",
                    Title = "Portable Expansion",
                    Platform = "windows"
                }
            ]
        };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "portable-game-id";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = storedInstallPath;
        item.CustomFields[CustomFieldKeyHelper.StoreDlcUpdateAvailable] = "true";
        item.MediaType = MediaType.Emulator;
        item.EmulatorId = "stale-emulator";
        item.LauncherPath = "umu-run";
        item.LauncherArgs = "{file}";
        item.RunnerVersionId = "proton-runner";
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
