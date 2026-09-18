using System.Text.Json;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogDlcCatalogTests
{
    [Fact]
    public void ParseOwnedDlcCatalog_ReturnsOnlyOwnedDlcsAndTheirInstallerPlatforms()
    {
        using var productJson = JsonDocument.Parse(
            """
            {
              "expanded_dlcs": [
                {
                  "id": 1001,
                  "title": "Owned Windows DLC",
                  "downloads": {
                    "installers": [
                      {
                        "os": "windows",
                        "files": [
                          { "downlink": "https://api.gog.com/products/1001/downlink/installer/1" }
                        ]
                      }
                    ]
                  }
                },
                {
                  "id": "1002",
                  "title": "Owned Cross-platform DLC",
                  "downloads": {
                    "installers": [
                      {
                        "os": "linux",
                        "files": [
                          { "downlink": "https://api.gog.com/products/1002/downlink/installer/1" }
                        ]
                      },
                      {
                        "os": "windows",
                        "files": [
                          { "downlink": "https://api.gog.com/products/1002/downlink/installer/2" }
                        ]
                      }
                    ]
                  }
                },
                {
                  "id": 1003,
                  "title": "Not Owned DLC",
                  "downloads": { "installers": [] }
                }
              ]
            }
            """);
        using var ownedProductsJson = JsonDocument.Parse("""{ "owned": [1001, "1002"] }""");

        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            ownedProductsJson.RootElement);

        Assert.Collection(
            result,
            dlc =>
            {
                Assert.Equal("1001", dlc.ProductId);
                Assert.Equal("Owned Windows DLC", dlc.Title);
                Assert.Equal([GogInstallPlatform.Windows], dlc.AvailableInstallerPlatforms);
                Assert.True(dlc.HasInstaller);
            },
            dlc =>
            {
                Assert.Equal("1002", dlc.ProductId);
                Assert.Equal("Owned Cross-platform DLC", dlc.Title);
                Assert.Equal(
                    [GogInstallPlatform.Linux, GogInstallPlatform.Windows],
                    dlc.AvailableInstallerPlatforms);
                Assert.True(dlc.HasInstaller);
            });
    }

    [Fact]
    public void ParseOwnedDlcCatalog_KeepsOwnedDlcWithoutSeparateInstaller()
    {
        using var productJson = JsonDocument.Parse(
            """
            {
              "expanded_dlcs": [
                {
                  "id": 2001,
                  "title": "Depot-less DLC",
                  "downloads": { "installers": [] }
                }
              ]
            }
            """);
        using var ownedProductsJson = JsonDocument.Parse("""{ "owned": [2001] }""");

        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            ownedProductsJson.RootElement);

        var dlc = Assert.Single(result);
        Assert.Equal("2001", dlc.ProductId);
        Assert.False(dlc.HasInstaller);
        Assert.Empty(dlc.AvailableInstallerPlatforms);
    }

    [Fact]
    public void ParseOwnedDlcCatalog_WithoutDlcExpansion_ReturnsEmptyCatalog()
    {
        using var productJson = JsonDocument.Parse("""{ "id": 3000 }""");
        using var ownedProductsJson = JsonDocument.Parse("""{ "owned": [3001] }""");

        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            ownedProductsJson.RootElement);

        Assert.Empty(result);
    }
}
