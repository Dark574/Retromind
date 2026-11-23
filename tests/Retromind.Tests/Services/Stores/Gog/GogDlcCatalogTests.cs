using System.Net;
using System.Text.Json;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

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
                        "version": "1.2.3",
                        "files": [
                          { "downlink": "https://api.gog.com/products/1001/downlink/installer/1", "size": 1234 }
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
        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            new HashSet<string> { "1001", "1002" });

        Assert.Collection(
            result,
            dlc =>
            {
                Assert.Equal("1001", dlc.ProductId);
                Assert.Equal("Owned Windows DLC", dlc.Title);
                Assert.Equal([GogInstallPlatform.Windows], dlc.AvailableInstallerPlatforms);
                Assert.True(dlc.HasInstaller);
                var metadata = Assert.Single(dlc.InstallerMetadata!);
                Assert.Equal(GogInstallPlatform.Windows, metadata.Platform);
                Assert.Equal("1.2.3", metadata.Version);
                Assert.StartsWith(GogDlcUpdateComparer.CatalogSignaturePrefix, metadata.Signature);
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
        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            new HashSet<string> { "2001" });

        var dlc = Assert.Single(result);
        Assert.Equal("2001", dlc.ProductId);
        Assert.False(dlc.HasInstaller);
        Assert.Empty(dlc.AvailableInstallerPlatforms);
    }

    [Fact]
    public void ParseOwnedDlcCatalog_WithoutDlcExpansion_ReturnsEmptyCatalog()
    {
        using var productJson = JsonDocument.Parse("""{ "id": 3000 }""");
        var result = GogInstallService.ParseOwnedDlcCatalog(
            productJson.RootElement,
            new HashSet<string> { "3001" });

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetOwnedDlcCatalogAsync_FillsOwnedReferencesAndKeepsUnavailableEntries(HttpStatusCode errorStatus)
    {
        var handler = new DlcApiHandler(errorStatus);
        using var client = new HttpClient(handler);
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(new SecretKey("retromind:gog", "default"), "test-refresh");
        var auth = new GogAuthService(secrets, new GogOAuthClient(client), new GogPkceService(), new RecordingErrorService());
        var service = new GogInstallService(auth, client, client);

        var catalog = await service.GetOwnedDlcCatalogAsync("1000");

        Assert.Equal(["/products/1004", "/products/1002"], handler.DetailRequests);
        Assert.Collection(
            catalog,
            item =>
            {
                Assert.Equal("1001", item.ProductId);
                Assert.Equal("Expanded DLC", item.Title);
                Assert.False(item.DetailsUnavailable);
            },
            item =>
            {
                Assert.Equal("1004", item.ProductId);
                Assert.Equal("GOG DLC 1004", item.Title);
                Assert.False(item.HasInstaller);
                Assert.True(item.DetailsUnavailable);
            },
            item =>
            {
                Assert.Equal("1002", item.ProductId);
                Assert.Equal("Referenced DLC", item.Title);
                Assert.False(item.DetailsUnavailable);
                Assert.Equal("1.2.3", item.GetInstallerMetadata(GogInstallPlatform.Linux)?.Version);
            });
    }

    private sealed class DlcApiHandler(HttpStatusCode errorStatus) : HttpMessageHandler
    {
        public List<string> DetailRequests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath != "/token")
                Assert.Equal("Bearer test-access", request.Headers.Authorization?.ToString());

            var response = (uri.Host, uri.PathAndQuery) switch
            {
                ("auth.gog.com", "/token") => JsonResponse(
                    """{"access_token":"test-access","refresh_token":"test-refresh","user_id":"test-user","expires_in":3600}"""),
                ("embed.gog.com", "/user/data/games") => JsonResponse(
                    """{"owned":[1001,"1002",1004,"../1006"]}"""),
                ("api.gog.com", "/products/1000?expand=downloads,expanded_dlcs") => JsonResponse(
                    """
                    {
                      "expanded_dlcs": [
                        {"id":1001,"title":"Expanded DLC"},
                        {"id":"1001","title":"Duplicate DLC"}
                      ],
                      "dlcs": {"products":[
                        {"id":1001},
                        {"id":1003},
                        {"id":"../1006"},
                        {"id":1004},
                        {"id":1002,"expanded_link":"https://example.com/foreign"},
                        {"id":"1002"}
                      ]}
                    }
                    """),
                ("api.gog.com", "/products/1004?expand=downloads") => new HttpResponseMessage(errorStatus),
                ("api.gog.com", "/products/1002?expand=downloads") => JsonResponse(
                    """
                    {
                      "id":1002,"title":"Referenced DLC",
                      "downloads":{"installers":[
                        {"os":"linux","version":"1.2.3","files":[{"downlink":"https://api.gog.com/installer"}]}
                      ]}
                    }
                    """),
                _ => throw new InvalidOperationException("Unexpected GOG endpoint.")
            };

            if (uri.Query == "?expand=downloads")
                DetailRequests.Add(uri.AbsolutePath);

            return Task.FromResult(response);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }
}
