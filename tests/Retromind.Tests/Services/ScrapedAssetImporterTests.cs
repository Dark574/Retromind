using System.Net;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Scrapers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ScrapedAssetImporterTests
{
    [Theory]
    [InlineData("disabled", 0)]
    [InlineData("no-url", 0)]
    [InlineData("existing", 0)]
    [InlineData("existing-append", 1)]
    [InlineData("download-failure", 1)]
    public async Task ImportAsync_ReportsActualFailureSeparatelyFromPolicySkips(string mode, int expectedFailures)
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        var importer = new ScrapedAssetImporter(client, new FileManagementService(temp.RootPath, errors), errors);
        var item = new MediaItem("Fixture");
        if (mode is "existing" or "existing-append")
            item.Assets.Add(new MediaAsset { Type = AssetType.Cover });
        var result = new ScraperSearchResult
        {
            CoverUrl = mode == "no-url" ? null : "https://images.example/fixture.png"
        };
        var settings = new ScraperImportSettings { ImportCover = mode != "disabled" };
        var failed = new List<AssetType>();

        var outcome = await importer.ImportAsync(
            item, result, ["Games"], settings, appendAssetsWhenTypeExists: mode == "existing-append",
            onAssetImportFailed: failed.Add);

        Assert.False(outcome.Changed);
        Assert.Null(outcome.DocumentImportError);
        Assert.Equal(expectedFailures, handler.RequestCount);
        Assert.Equal(expectedFailures, failed.Count);
        Assert.All(failed, type => Assert.Equal(AssetType.Cover, type));
    }

    [Fact]
    public async Task ImportAsync_ContinuesAfterDocumentFailureAndSkipsDuplicatePdfContents()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var errors = new RecordingErrorService { ThrowOnReport = true };
        const string pdf = "%PDF-1.4\nFixture\n%%EOF\n";
        using var handler = new RecordingHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/invalid"
                ? "<html>private-response</html>"
                : pdf)
        });
        using var client = new HttpClient(handler);
        var importer = new ScrapedAssetImporter(client, new FileManagementService(AppPaths.LibraryRoot, errors), errors);
        var item = new MediaItem("Fixture");
        var result = new ScraperSearchResult
        {
            Documents =
            [
                new ScraperDocument { Url = "https://example.test/invalid?password=private-token" },
                new ScraperDocument { Url = "https://example.test/manual.pdf", Title = "Manual" },
                new ScraperDocument { Url = "https://example.test/duplicate.pdf", Title = "Other title" }
            ]
        };

        var outcome = await importer.ImportAsync(
            item, result, ["Games"], new ScraperImportSettings { ImportManuals = true }, appendAssetsWhenTypeExists: false);

        Assert.True(outcome.Changed);
        Assert.NotNull(outcome.DocumentImportError);
        Assert.Equal(3, handler.RequestCount);
        var asset = Assert.Single(item.Assets);
        Assert.Equal(AssetType.Manual, asset.Type);
        Assert.False(Path.IsPathRooted(asset.RelativePath));
        Assert.Equal(pdf, await File.ReadAllTextAsync(asset.AbsolutePath));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Scraper.DocumentImportFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal(nameof(ScrapedAssetImporter), report.Component);
        Assert.Null(report.Exception);
        Assert.NotNull(report.Context);
        Assert.Equal("NotPdf", report.Context["FailureReason"]);
        Assert.DoesNotContain("private-", string.Join(" ", report.Context.Values));
        Assert.Null(Assert.Single(errors.Notifications));
    }

    [Theory]
    [InlineData("NotPdf")]
    [InlineData("SizeLimitExceeded")]
    [InlineData("IncompleteDownload")]
    public void DocumentFailureDescriptionIsSpecificAndSafe(string reason)
    {
        var error = new ScrapedDocumentDownloadException(reason, 200);
        var generic = ScrapedAssetImporter.DescribeDocumentImportFailure(new IOException("password=private-secret"));
        var message = ScrapedAssetImporter.DescribeDocumentImportFailure(error);

        Assert.NotEqual(generic, message);
        Assert.DoesNotContain("private-secret", generic);
        Assert.DoesNotContain("private-secret", ScrapedAssetImporter.DescribeDocumentImportFailure(
            new HttpRequestException("password=private-secret", null, HttpStatusCode.Forbidden)));
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }
}
