using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class StoreImportServiceTests
{
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
}
