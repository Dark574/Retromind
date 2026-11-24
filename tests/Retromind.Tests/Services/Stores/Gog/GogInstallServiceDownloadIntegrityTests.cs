using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallServiceDownloadIntegrityTests
{
    private static readonly Uri DownloadUri = new("https://cdn.gog.test/setup_game.sh");
    private static readonly Uri ChecksumUri = new("https://cdn.gog.test/setup_game.sh.xml");

    [Theory]
    [InlineData("http://api.gog.com/part")]
    [InlineData("https://cdn.gog.test/part")]
    [InlineData("https://api.gog.com:8443/part")]
    [InlineData("https://user@api.gog.com/part")]
    public async Task ResolveInstallerPackage_RejectsUnexpectedBearerDestination(string endpoint)
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.Host switch
        {
            "auth.gog.com" => TokenResponse(),
            "api.gog.com" when request.RequestUri.AbsolutePath == "/products/123" => ProductResponse(endpoint),
            _ => throw new InvalidOperationException("The request must stop before sending the bearer token.")
        }));
        var service = await CreateAuthenticatedServiceAsync(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ResolveInstallerPackageAsync("123", GogInstallPlatform.Linux));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveInstallerPackage_RequiresEveryPart(bool secondAvailable)
    {
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => TokenResponse(),
            "/products/123" => ProductResponse("https://api.gog.com/first", "https://api.gog.com/second"),
            "/first" => JsonResponse("{\"downlink\":\"https://cdn.gog.test/setup.sh\"}"),
            "/second" when secondAvailable => JsonResponse("{\"downlink\":\"https://cdn.gog.test/setup.bin\"}"),
            "/second" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        }));
        var service = await CreateAuthenticatedServiceAsync(client);

        if (secondAvailable)
        {
            var package = await service.ResolveInstallerPackageAsync("123", GogInstallPlatform.Linux);
            Assert.Equal(["setup.sh", "setup.bin"], package!.Files.Select(file => file.FileName));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                service.ResolveInstallerPackageAsync("123", GogInstallPlatform.Linux));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        }
    }

    private static async Task<GogInstallService> CreateAuthenticatedServiceAsync(HttpClient client)
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(new SecretKey("retromind:gog", "default"), "test-refresh");
        var auth = new GogAuthService(secrets, new GogOAuthClient(client), new GogPkceService(), new RecordingErrorService());
        return new GogInstallService(auth, client, client);
    }

    private static HttpResponseMessage ProductResponse(params string[] endpoints) => JsonResponse(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            downloads = new { installers = new[] { new
            {
                os = "linux", files = endpoints.Select(downlink => new { downlink, size = 4 })
            } } }
        }));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QueuedDownload_UsesSharedGameFolderAndRecognizesLegacyArchive(bool legacy, bool update)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var transfers = 0;
        using var client = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => TokenResponse(),
            "/products/123" => ProductResponse("https://api.gog.com/first"),
            "/first" => JsonResponse("{\"downlink\":\"https://cdn.gog.test/setup.sh\"}"),
            "/setup.sh" => Download(),
            _ => throw new InvalidOperationException("Unexpected endpoint.")
        }));
        HttpResponseMessage Download()
        {
            transfers++;
            return BytesResponse(Encoding.UTF8.GetBytes("game"));
        }
        var installer = await CreateAuthenticatedServiceAsync(client);
        var package = (await installer.ResolveInstallerPackageAsync("123", GogInstallPlatform.Linux))!;
        var settings = new DownloadSettings { KeepGogInstallers = true, GogArchiveDirectory = "Offline" };
        var archive = GogInstallerArchive.GetPackageDirectory(settings, package, gameTitle: "My Game");
        Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(archive, "dlc.sh"), "existing DLC");
        var oldArchive = GogInstallerArchive.GetLegacyPackageDirectory(settings, package);
        if (legacy)
        {
            Directory.CreateDirectory(oldArchive);
            await File.WriteAllTextAsync(Path.Combine(oldArchive, "setup.sh"), "game");
        }
        var job = new DownloadJob(Guid.NewGuid(), "item", DownloadStore.Gog,
            update ? DownloadKind.Update : DownloadKind.OfflineInstaller, "revision", false, false, false,
            DownloadState.Queued);
        var execution = new DownloadExecution(job, settings, false, _ => { }, () => true);

        var result = await new GogQueuedDownloadService(installer).DownloadAsync(execution, "123",
            GogInstallPlatform.Linux, null, null, CancellationToken.None, "My Game");

        Assert.Equal(update ? DownloadState.InstallationPending : DownloadState.Completed, result.State);
        Assert.True(result.PayloadReady);
        Assert.Equal(GogInstallerArchive.GetRevision(package), result.PayloadRevision);
        Assert.Equal(legacy ? 0 : 1, transfers);
        Assert.Equal("existing DLC", await File.ReadAllTextAsync(Path.Combine(archive, "dlc.sh")));
        Assert.Equal("game", await File.ReadAllTextAsync(Path.Combine(legacy ? oldArchive : archive, "setup.sh")));
        Assert.Empty(Directory.GetDirectories(archive));

        // A manual reinstall can reuse either layout even when the shared folder only contains DLCs.
        var prepared = await installer.DownloadInstallerPackageAsync(package, temp.GetPath("install-staging"),
            null, null, GogInstallerArchive.GetStagingDirectory(package), CancellationToken.None,
            archiveDirectory: archive, requireCachedPayload: true, legacyArchiveDirectory: oldArchive);
        Assert.Equal("game", await File.ReadAllTextAsync(prepared.EntryFilePath));
        Assert.Equal(legacy ? 0 : 1, transfers);
    }

    private static HttpResponseMessage TokenResponse() => JsonResponse(
        """{"access_token":"test-access","refresh_token":"test-refresh","user_id":"test-user","expires_in":3600}""");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
    public async Task ArchiveQueuedPackage_ReleasesOnlyArchivedFilesAndLaterInstallReusesArchive()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var package = CreatePackage(catalogSize: payload.Length);
        var staging = GogInstallerArchive.GetStagingDirectory(package);
        var archive = GogInstallerArchive.GetPackageDirectory(new DownloadSettings { GogArchiveDirectory = "Offline" }, package);
        var networkDownloads = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(payload);

            networkDownloads++;
            return BytesResponse(payload);
        }));
        var service = CreateService(client);
        var downloaded = await service.DownloadInstallerPackageAsync(package, staging);
        var unrelated = Path.Combine(staging, "unrelated.part");
        await File.WriteAllTextAsync(unrelated, "keep");

        await service.ArchiveQueuedPackageAsync(package, archive, downloaded, CancellationToken.None);

        Assert.False(File.Exists(downloaded.EntryFilePath));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        var archivedFile = Path.Combine(archive, Path.GetFileName(downloaded.EntryFilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(archivedFile));

        var working = await service.DownloadInstallerPackageAsync(package, temp.GetPath("install-staging"),
            null, null, staging, CancellationToken.None, archiveDirectory: archive, requireCachedPayload: true);
        Assert.Equal(1, networkDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(working.EntryFilePath));

        await File.WriteAllBytesAsync(downloaded.EntryFilePath, new byte[payload.Length]);
        await Assert.ThrowsAsync<IOException>(() =>
            service.ArchiveQueuedPackageAsync(package, archive, downloaded, CancellationToken.None));
        Assert.Equal(new byte[payload.Length], await File.ReadAllBytesAsync(downloaded.EntryFilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(archivedFile));
    }

    [Fact]
    public async Task DeleteInstalledQueuedStaging_RemovesOnlyInstalledRevision()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var package = CreatePackage(catalogSize: 4);
        var installed = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        var newer = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package with { Version = "new" })).FullName;
        await File.WriteAllTextAsync(Path.Combine(installed, "setup_game.sh"), "game");
        await File.WriteAllTextAsync(Path.Combine(newer, "setup_game.sh"), "next");
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected download.")));

        await CreateService(client).DeleteInstalledQueuedStagingAsync(package);

        Assert.False(Directory.Exists(installed));
        Assert.Equal("next", await File.ReadAllTextAsync(Path.Combine(newer, "setup_game.sh")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task DeleteInstalledQueuedStaging_DefersCleanupUntilQueuedWorkSettles(bool? archiveSucceeds)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var package = CreatePackage(catalogSize: 4);
        var staging = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        var file = Path.Combine(staging, "setup_game.sh");
        await File.WriteAllTextAsync(file, "game");
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected download.")));
        var service = CreateService(client);
        var archive = GogInstallerArchive.GetPackageDirectory(new(), package);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = service.UseQueuedStagingAsync(package, async () =>
        {
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (archiveSucceeds == false)
                return new DownloadOutcome(DownloadState.Failed, DownloadReason.ArchiveUnavailable);
            if (archiveSucceeds == true)
                await service.ArchiveQueuedPackageAsync(package, archive, new(staging, file, [file]), CancellationToken.None);
            return new DownloadOutcome(DownloadState.Completed, PayloadReady: true);
        });

        try
        {
            await service.DeleteInstalledQueuedStagingAsync(package).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(file));
        }
        finally
        {
            release.TrySetResult();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(archiveSucceeds == false, File.Exists(file));
        Assert.Equal(archiveSucceeds == true, File.Exists(Path.Combine(archive, "setup_game.sh")));
    }

    [Fact]
    public async Task RequireCachedPayload_DoesNotDownloadMissingOrCorruptInstallerBytes()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var package = CreatePackage(catalogSize: payload.Length);
        var staging = temp.CreateDirectory("staging");
        var cache = temp.CreateDirectory("cache");
        var file = Path.Combine(cache, GogInstallerArchive.GetSafeFileNames(package)[0]);
        await File.WriteAllBytesAsync(file, new byte[payload.Length]);
        var networkDownloads = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return ChecksumResponse(payload);
            networkDownloads++;
            return BytesResponse(payload);
        }));
        var service = CreateService(client);

        await Assert.ThrowsAsync<GogInstallerPayloadUnavailableException>(() =>
            service.DownloadInstallerPackageAsync(package, staging, null, null, cache, CancellationToken.None, requireCachedPayload: true));
        File.Delete(file);
        await Assert.ThrowsAsync<GogInstallerPayloadUnavailableException>(() =>
            service.DownloadInstallerPackageAsync(package, staging, null, null, cache, CancellationToken.None, requireCachedPayload: true));

        Assert.Equal(0, networkDownloads);
        Assert.Empty(Directory.EnumerateFiles(staging));
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

    [Fact]
    public async Task DownloadInstallerPackage_StreamExceedsDeclaredLength_StopsAtFirstOverflowByte()
    {
        using var temp = new TemporaryDirectory();
        var payload = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var stagingPath = temp.CreateDirectory("staging");
        var source = new TrackingMemoryStream(payload);
        using var downloadClient = new HttpClient(new StubHandler(_ => StreamResponse(source, 8)));
        var service = CreateService(downloadClient);
        var package = new GogInstallerPackage(
            "game-id",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile(
                DownloadUri.ToString(),
                "setup_game.sh",
                payload.Length)]);

        var exception = await Assert.ThrowsAsync<GogInstallerDownloadLimitExceededException>(() =>
            service.DownloadInstallerPackageAsync(package, stagingPath));

        Assert.Contains("allowed download size", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, source.BytesRead);
        Assert.False(File.Exists(Path.Combine(stagingPath, "setup_game.sh")));
        Assert.False(File.Exists(Path.Combine(stagingPath, "setup_game.sh.part")));
    }

    [Fact]
    public async Task DownloadInstallerPackage_DeclaredLengthExceedsHardLimit_RejectsBeforeReading()
    {
        using var temp = new TemporaryDirectory();
        var payload = Enumerable.Range(0, 33).Select(value => (byte)value).ToArray();
        var stagingPath = temp.CreateDirectory("staging");
        var source = new TrackingMemoryStream(payload);
        using var downloadClient = new HttpClient(new StubHandler(_ => StreamResponse(source, payload.Length)));
        var service = CreateService(
            downloadClient,
            maxInstallerFileDownloadBytes: 32,
            maxChecksumMetadataBytes: GogInstallService.MaxChecksumMetadataBytes);
        var package = new GogInstallerPackage(
            "game-id",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile(
                DownloadUri.ToString(),
                "setup_game.sh",
                null)]);

        await Assert.ThrowsAsync<GogInstallerDownloadLimitExceededException>(() =>
            service.DownloadInstallerPackageAsync(package, stagingPath));

        Assert.Equal(0, source.BytesRead);
        Assert.False(File.Exists(Path.Combine(stagingPath, "setup_game.sh.part")));
    }

    [Fact]
    public async Task DownloadInstallerPackage_ChecksumMetadataStopsAtFirstOverflowByte()
    {
        using var temp = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("installer payload");
        var oversizedMetadata = Enumerable.Repeat((byte)'x', 64).ToArray();
        var checksumSource = new TrackingMemoryStream(oversizedMetadata);
        var stagingPath = temp.CreateDirectory("staging");
        using var downloadClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri == ChecksumUri)
                return StreamResponse(checksumSource, 1);

            return BytesResponse(payload);
        }));
        var service = CreateService(
            downloadClient,
            maxInstallerFileDownloadBytes: 1024,
            maxChecksumMetadataBytes: 32);

        var result = await service.DownloadInstallerPackageAsync(
            CreatePackage(catalogSize: payload.Length),
            stagingPath);

        Assert.Equal(33, checksumSource.BytesRead);
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

    private static GogInstallService CreateService(
        HttpClient downloadClient,
        long maxInstallerFileDownloadBytes = GogInstallService.MaxInstallerFileDownloadBytes,
        int maxChecksumMetadataBytes = GogInstallService.MaxChecksumMetadataBytes)
    {
        var providerClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("Provider HTTP was not expected.")));
        var authService = new GogAuthService(
            new InMemorySecretStore(),
            new GogOAuthClient(providerClient),
            new GogPkceService(), new RecordingErrorService());
        return new GogInstallService(
            authService,
            providerClient,
            downloadClient,
            maxInstallerFileDownloadBytes,
            maxChecksumMetadataBytes);
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

    private static HttpResponseMessage StreamResponse(Stream content, long declaredLength)
    {
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = streamContent
        };
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> target,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await base.ReadAsync(target, cancellationToken);
            BytesRead += bytesRead;
            return bytesRead;
        }
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
