using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class StoreImportServiceTests
{
    [Fact]
    public async Task SteamImport_ContinuesWhenADiscoveredLibraryDisappears()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.CreateDirectory("first", "steamapps");
        var second = temp.CreateDirectory("second", "steamapps");
        temp.CreateFile("second/steamapps/appmanifest_78902.acf", "\"appid\" \"78902\"\n\"name\" \"Second library game\"");
        var service = new StoreImportService(new AppSettings { SteamLibraryPaths = ["invalid\0path", second] });
        var discovered = new DiscoveredLibraries(path =>
        {
            if (path == first)
                Directory.Move(first, temp.GetPath("unmounted-steamapps"));
        });

        var items = await service.ImportSteamGamesAsync(first, discovered);

        Assert.Equal(first, discovered[0]);
        Assert.Contains(second, discovered);
        Assert.False(Directory.Exists(first));
        Assert.Single(items, item => item.Title == "Second library game");
    }

    [Fact]
    public async Task SteamImport_PersistsStoreIdentity()
    {
        using var temp = new TemporaryDirectory();
        var steamAppsPath = temp.CreateDirectory("steamapps");
        temp.CreateFile(
            "steamapps/appmanifest_123.acf",
            "\"AppState\"\n{\n\"appid\" \"123\"\n\"name\" \"Test Game\"\n}");
        var service = new StoreImportService(new AppSettings());

        var item = Assert.Single(
            await service.ImportSteamGamesAsync(steamAppsPath),
            candidate => candidate.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreGameId) == "123");

        Assert.Equal("steam", item.CustomFields[CustomFieldKeyHelper.StoreProviderId]);
        Assert.Equal("123", item.CustomFields[CustomFieldKeyHelper.StoreGameId]);
    }

    private sealed class DiscoveredLibraries(Action<string> onDiscovered) : Collection<string>
    {
        protected override void InsertItem(int index, string item)
        {
            base.InsertItem(index, item);
            onDiscovered(item);
        }
    }
}
