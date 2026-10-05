using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallServiceDownloadIntegrityTests
{
    private static readonly Uri DownloadUri = new("https://cdn.gog.test/setup_game.sh");
    private static readonly Uri ChecksumUri = new("https://cdn.gog.test/setup_game.sh.xml");

    [Fact]
    public void ParseResolvedDownlink_PreservesChecksumUrl()
    {
        var result = GogInstallService.ParseResolvedDownlink(
            $$"""
              {
                "downlink": "{{DownloadUri}}",
                "checksum": "{{ChecksumUri}}"
              }
              """);

        Assert.NotNull(result);
        Assert.Equal(DownloadUri.ToString(), result.DownloadUrl);
        Assert.Equal(ChecksumUri.ToString(), result.ChecksumUrl);
    }

    [Fact]
    public void TryParseDownloadChecksum_ReadsWholeFileMetadata()
    {
        const string md5 = "00112233445566778899aabbccddeeff";

        var parsed = GogInstallService.TryParseDownloadChecksum(
            $$"""
              <file name="setup.bin" md5="{{md5}}" chunks="1" total_size="12345">
                <chunk id="0" from="0" to="12344" method="md5">{{md5}}</chunk>
              </file>
              """,
            out var checksum);

        Assert.True(parsed);
        Assert.Equal(12345, checksum.TotalSize);
        Assert.Equal(md5.ToUpperInvariant(), checksum.Md5);
    }

    [Fact]
    public async Task DownloadInstallerPackage_ValidCompletePart_IsPromotedUsingChecksumMetadata()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("complete installer payload");
        var stagingPath = temp.CreateDirectory("staging");
        var targetPath = Path.Combine(stagingPath, "setup_game.sh");
        await File.WriteAllBytesAsync(targetPath + ".part", payload);
        var downloadRequests = 0;
        using var downloadClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(payload);

            downloadRequests++;
            return BytesResponse(payload);
        }));
        var service = CreateService(downloadClient);

        var result = await service.DownloadInstallerPackageAsync(
            CreatePackage(catalogSize: payload.Length + 64),
            stagingPath);

        Assert.Equal(0, downloadRequests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.EntryFilePath));
        Assert.False(File.Exists(targetPath + ".part"));
    }

    [Fact]
    public async Task DownloadInstallerPackage_CorruptCachedFile_IsDownloadedAgain()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("expected installer payload");
        var corruptPayload = Encoding.UTF8.GetBytes("corrupt! installer payload");
        Assert.Equal(payload.Length, corruptPayload.Length);
        var stagingPath = temp.CreateDirectory("staging");
        var targetPath = Path.Combine(stagingPath, "setup_game.sh");
        await File.WriteAllBytesAsync(targetPath, corruptPayload);
        var downloadRequests = 0;
        using var downloadClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(payload);

            downloadRequests++;
            return BytesResponse(payload);
        }));
        var service = CreateService(downloadClient);

        var result = await service.DownloadInstallerPackageAsync(
            CreatePackage(catalogSize: payload.Length),
            stagingPath);

        Assert.Equal(1, downloadRequests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.EntryFilePath));
    }

    [Fact]
    public async Task DownloadInstallerPackage_PartialFile_ResumesAtExactOffsetAndVerifiesHash()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("resumable installer payload");
        var partialLength = 9;
        var stagingPath = temp.CreateDirectory("staging");
        var targetPath = Path.Combine(stagingPath, "setup_game.sh");
        await File.WriteAllBytesAsync(targetPath + ".part", payload[..partialLength]);
        long? requestedOffset = null;
        using var downloadClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(payload);

            requestedOffset = request.Headers.Range?.Ranges.Single().From;
            var response = BytesResponse(payload[partialLength..], HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                partialLength,
                payload.Length - 1,
                payload.Length);
            return response;
        }));
        var service = CreateService(downloadClient);

        var result = await service.DownloadInstallerPackageAsync(
            CreatePackage(catalogSize: payload.Length),
            stagingPath);

        Assert.Equal(partialLength, requestedOffset);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.EntryFilePath));
    }

    [Fact]
    public async Task DownloadInstallerPackage_RepeatedChecksumMismatch_FailsWithoutPromotingPart()
    {
        using var temp = new TemporaryDirectory();
        var expectedPayload = Encoding.UTF8.GetBytes("expected installer payload");
        var corruptPayload = Encoding.UTF8.GetBytes("corrupt! installer payload");
        Assert.Equal(expectedPayload.Length, corruptPayload.Length);
        var stagingPath = temp.CreateDirectory("staging");
        var downloadRequests = 0;
        using var downloadClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(expectedPayload);

            downloadRequests++;
            return BytesResponse(corruptPayload);
        }));
        var service = CreateService(downloadClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadInstallerPackageAsync(
                CreatePackage(catalogSize: expectedPayload.Length),
                stagingPath));

        Assert.Contains("integrity check", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, downloadRequests);
        Assert.False(File.Exists(Path.Combine(stagingPath, "setup_game.sh")));
        Assert.False(File.Exists(Path.Combine(stagingPath, "setup_game.sh.part")));
    }

    [Fact]
    public async Task DownloadInstallerPackage_WithoutChecksum_UsesCompletedHttpLength()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("http validated installer");
        var stagingPath = temp.CreateDirectory("staging");
        using var downloadClient = new HttpClient(new StubHandler(_ => BytesResponse(payload)));
        var service = CreateService(downloadClient);
        var package = new GogInstallerPackage(
            "game-id",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile(
                DownloadUri.ToString(),
                "setup_game.sh",
                payload.Length + 123)]);

        var result = await service.DownloadInstallerPackageAsync(package, stagingPath);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.EntryFilePath));
    }

    private static GogInstallerPackage CreatePackage(long? catalogSize = null)
    {
        return new GogInstallerPackage(
            "game-id",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile(
                DownloadUri.ToString(),
                "setup_game.sh",
                catalogSize,
                ChecksumUri.ToString())]);
    }

    private static GogInstallService CreateService(HttpClient downloadClient)
    {
        var providerClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("Provider HTTP was not expected.")));
        var authService = new GogAuthService(
            new InMemorySecretStore(),
            new GogOAuthClient(providerClient),
            new GogPkceService());
        return new GogInstallService(authService, providerClient, downloadClient);
    }

    private static HttpResponseMessage ChecksumResponse(byte[] payload)
    {
        var md5 = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<file name=\"setup_game.sh\" md5=\"{md5}\" chunks=\"1\" total_size=\"{payload.Length}\" />",
                Encoding.UTF8,
                "application/xml")
        };
    }

    private static HttpResponseMessage BytesResponse(
        byte[] payload,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(payload)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
