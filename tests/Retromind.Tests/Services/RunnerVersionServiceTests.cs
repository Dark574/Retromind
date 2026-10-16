using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class RunnerVersionServiceTests
{
    private const string ReleaseTag = "GE-Proton10-1";
    private const string ReleaseAssetName = "GE-Proton10-1.tar.gz";
    private const string ReleaseDownloadUrl =
        "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton10-1/GE-Proton10-1.tar.gz";
    private const string ReleaseChecksumUrl =
        "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton10-1/GE-Proton10-1.sha512sum";

    [Fact]
    public async Task GetGeProtonReleases_FiltersDraftAndArmAssets()
    {
        const string json = """
            [
              {
                "tag_name": "GE-Proton10-1",
                "draft": false,
                "assets": [
                  {
                    "name": "GE-Proton10-1.tar.gz",
                    "browser_download_url": "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton10-1/GE-Proton10-1.tar.gz"
                  },
                  {
                    "name": "GE-Proton10-1.sha512sum",
                    "browser_download_url": "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton10-1/GE-Proton10-1.sha512sum"
                  }
                ]
              },
              {
                "tag_name": "GE-Proton10-2",
                "draft": true,
                "assets": [
                  {
                    "name": "GE-Proton10-2.tar.gz",
                    "browser_download_url": "https://example.invalid/draft.tar.gz"
                  }
                ]
              },
              {
                "tag_name": "GE-Proton10-3",
                "draft": false,
                "assets": [
                  {
                    "name": "GE-Proton10-3-aarch64.tar.gz",
                    "browser_download_url": "https://example.invalid/arm.tar.gz"
                  }
                ]
              }
            ]
            """;

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) => JsonResponse(json)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var releases = await service.GetGeProtonReleasesAsync();

        var release = Assert.Single(releases);
        Assert.Equal("GE-Proton10-1", release.TagName);
        Assert.Equal("GE-Proton10-1.tar.gz", release.AssetName);
        Assert.Equal(ReleaseDownloadUrl, release.DownloadUrl);
        Assert.Equal(ReleaseChecksumUrl, release.ChecksumUrl);
    }

    [Fact]
    public async Task GetGeProtonReleases_SkipsReleaseWithoutMatchingChecksumAsset()
    {
        const string json = """
            [
              {
                "tag_name": "GE-Proton10-1",
                "draft": false,
                "assets": [
                  {
                    "name": "GE-Proton10-1.tar.gz",
                    "browser_download_url": "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton10-1/GE-Proton10-1.tar.gz"
                  }
                ]
              }
            ]
            """;

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) => JsonResponse(json)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var releases = await service.GetGeProtonReleasesAsync();

        Assert.Empty(releases);
    }

    [Fact]
    public async Task GetGeProtonReleases_RejectsRedirectOutsideTrustedApiHost()
    {
        var requestCount = 0;
        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return RedirectResponse("https://example.invalid/releases");
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetGeProtonReleasesAsync());

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_PublishesOnlyValidatedRunner()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var relativePath = await service.DownloadAndInstallGeProtonAsync(CreateRelease());

        Assert.Equal("Emulators/ProtonVersions/GE-Proton10-1", relativePath);
        Assert.True(File.Exists(temp.GetPath(relativePath, "proton")));
        Assert.True(File.Exists(temp.GetPath(relativePath, "toolmanifest.vdf")));
        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsChecksumMismatchBeforeExtraction()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            IsChecksumRequest(request)
                ? ChecksumResponse([0x01, 0x02, 0x03])
                : BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        var runnerRoot = temp.GetPath("Emulators", "ProtonVersions");
        Assert.False(Directory.Exists(Path.Combine(runnerRoot, ReleaseTag)));
        Assert.Empty(Directory.GetDirectories(runnerRoot, ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsChecksumForDifferentAssetBeforeArchiveDownload()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));
        var requestCount = 0;

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return ChecksumResponse(archive, "different.tar.gz");
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Equal(1, requestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAndInstallGeProton_RejectsUntrustedInitialAssetUrl(
        bool replaceChecksumUrl)
    {
        var requestCount = 0;
        var release = replaceChecksumUrl
            ? CreateRelease(checksumUrl: "https://example.invalid/GE-Proton10-1.sha512sum")
            : CreateRelease(downloadUrl: "https://example.invalid/GE-Proton10-1.tar.gz");

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            throw new InvalidOperationException("HTTP should not be used.");
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(release));

        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsRedirectOutsideGithubReleaseAssetHost()
    {
        var requestCount = 0;
        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return RedirectResponse("https://example.invalid/asset");
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_AllowsGithubReleaseAssetRedirects()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
        {
            if (string.Equals(
                    request.RequestUri?.IdnHost,
                    "github.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                var redirectedName = IsChecksumRequest(request)
                    ? "GE-Proton10-1.sha512sum"
                    : "GE-Proton10-1.tar.gz";
                return RedirectResponse(
                    $"https://release-assets.githubusercontent.com/test/{redirectedName}?token=test");
            }

            return ReleaseResponse(request, archive);
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var relativePath = await service.DownloadAndInstallGeProtonAsync(CreateRelease());

        Assert.True(File.Exists(temp.GetPath(relativePath, "proton")));
        Assert.True(File.Exists(temp.GetPath(relativePath, "toolmanifest.vdf")));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_ReinstallReplacesOnlyAfterValidation()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "new runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "new manifest"));

        using var temp = new TemporaryDirectory();
        var existingDirectory = temp.CreateDirectory(
            "Emulators",
            "ProtonVersions",
            "GE-Proton10-1");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/proton", "old runner");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/toolmanifest.vdf", "old manifest");
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(
            CreateRelease(),
            replaceExisting: true);

        Assert.Equal("new runner", File.ReadAllText(Path.Combine(existingDirectory, "proton")));
        Assert.Equal("new manifest", File.ReadAllText(Path.Combine(existingDirectory, "toolmanifest.vdf")));
        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".backup_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_ExistingRunnerIsNotOverwrittenByNormalInstall()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "new runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "new manifest"));

        using var temp = new TemporaryDirectory();
        var existingDirectory = temp.CreateDirectory(
            "Emulators",
            "ProtonVersions",
            "GE-Proton10-1");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/proton", "old runner");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/toolmanifest.vdf", "old manifest");
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(CreateRelease());

        Assert.Equal("old runner", File.ReadAllText(Path.Combine(existingDirectory, "proton")));
        Assert.Equal("old manifest", File.ReadAllText(Path.Combine(existingDirectory, "toolmanifest.vdf")));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_InvalidReinstallPreservesExistingRunner()
    {
        var archive = CreateTarGz(("GE-Proton10-1/proton", "incomplete runner"));

        using var temp = new TemporaryDirectory();
        var existingDirectory = temp.CreateDirectory(
            "Emulators",
            "ProtonVersions",
            "GE-Proton10-1");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/proton", "old runner");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/toolmanifest.vdf", "old manifest");
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndInstallGeProtonAsync(
                CreateRelease(),
                replaceExisting: true));

        Assert.Equal("old runner", File.ReadAllText(Path.Combine(existingDirectory, "proton")));
        Assert.Equal("old manifest", File.ReadAllText(Path.Combine(existingDirectory, "toolmanifest.vdf")));
        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".backup_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_ReportsDownloadAndInstallProgress()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));
        var reports = new List<GeProtonInstallProgress>();

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(
            CreateRelease(),
            progress: new InlineProgress<GeProtonInstallProgress>(reports.Add));

        Assert.Contains(reports, report =>
            report.Stage == GeProtonInstallStage.Downloading &&
            report.DownloadedBytes == archive.Length &&
            report.TotalBytes == archive.Length &&
            report.Percentage == 100d);
        Assert.Contains(reports, report => report.Stage == GeProtonInstallStage.Installing);
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsAnnouncedDownloadAboveLimit()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));
        var maximumDownloadBytes = archive.Length;

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
        {
            if (IsChecksumRequest(request))
                return ChecksumResponse(archive);

            var response = BinaryResponse(archive);
            response.Content.Headers.ContentLength = maximumDownloadBytes + 1;
            return response;
        }));
        using var service = new RunnerVersionService(
            temp.RootPath,
            httpClient,
            maximumDownloadBytes,
            maxExtractedBytes: 1024,
            maxArchiveEntries: 10);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Empty(Directory.GetFileSystemEntries(
            temp.GetPath("Emulators", "ProtonVersions")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAndInstallGeProton_EnforcesStreamingLimitWhenLengthIsMissingOrUnderstated(
        bool understateContentLength)
    {
        var payload = Enumerable.Repeat((byte)0x41, 65).ToArray();

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
        {
            if (IsChecksumRequest(request))
                return ChecksumResponse(payload);

            var content = new UnknownLengthContent(payload);
            if (understateContentLength)
                content.Headers.ContentLength = 1;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        }));
        using var service = new RunnerVersionService(
            temp.RootPath,
            httpClient,
            maxDownloadBytes: 64,
            maxExtractedBytes: 1024,
            maxArchiveEntries: 10);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Empty(Directory.GetFileSystemEntries(
            temp.GetPath("Emulators", "ProtonVersions")));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsExpandedPayloadAboveLimit()
    {
        const string runnerContent = "runner payload";
        const string manifestContent = "manifest";
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", runnerContent),
            ("GE-Proton10-1/toolmanifest.vdf", manifestContent));
        var extractedBytes = Encoding.UTF8.GetByteCount(runnerContent) +
                             Encoding.UTF8.GetByteCount(manifestContent);

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(
            temp.RootPath,
            httpClient,
            maxDownloadBytes: archive.Length,
            maxExtractedBytes: extractedBytes - 1,
            maxArchiveEntries: 10);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsTooManyArchiveEntries()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(
            temp.RootPath,
            httpClient,
            maxDownloadBytes: archive.Length,
            maxExtractedBytes: 1024,
            maxArchiveEntries: 1);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_AcceptsExactSizeAndEntryLimits()
    {
        const string runnerContent = "runner";
        const string manifestContent = "manifest";
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", runnerContent),
            ("GE-Proton10-1/toolmanifest.vdf", manifestContent));
        var extractedBytes = Encoding.UTF8.GetByteCount(runnerContent) +
                             Encoding.UTF8.GetByteCount(manifestContent);

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(
            temp.RootPath,
            httpClient,
            maxDownloadBytes: archive.Length,
            maxExtractedBytes: extractedBytes,
            maxArchiveEntries: 2);

        var relativePath = await service.DownloadAndInstallGeProtonAsync(CreateRelease());

        Assert.True(File.Exists(temp.GetPath(relativePath, "proton")));
        Assert.True(File.Exists(temp.GetPath(relativePath, "toolmanifest.vdf")));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_CanceledDownloadLeavesNoPublishedRunner()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));
        using var cancellation = new CancellationTokenSource();

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var progress = new InlineProgress<GeProtonInstallProgress>(report =>
        {
            if (report.Stage == GeProtonInstallStage.Downloading)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAndInstallGeProtonAsync(
                CreateRelease(),
                cancellation.Token,
                progress));

        var runnerRoot = temp.GetPath("Emulators", "ProtonVersions");
        Assert.False(Directory.Exists(Path.Combine(runnerRoot, "GE-Proton10-1")));
        Assert.Empty(Directory.GetDirectories(runnerRoot, ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_IncompleteArchiveLeavesNoPublishedRunner()
    {
        var archive = CreateTarGz(("GE-Proton10-1/proton", "runner"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
            ReleaseResponse(request, archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        var runnerRoot = temp.GetPath("Emulators", "ProtonVersions");
        Assert.False(Directory.Exists(Path.Combine(runnerRoot, "GE-Proton10-1")));
        Assert.Empty(Directory.GetDirectories(runnerRoot, ".tmp_ge_*"));
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_RejectsSymbolicLinkInManagedRootBeforeDownload()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        var externalEmulators = temp.CreateDirectory("ExternalEmulators");
        Directory.CreateDirectory(Path.Combine(externalEmulators, "ProtonVersions"));
        Directory.CreateSymbolicLink(temp.GetPath("Emulators"), externalEmulators);
        var requestSent = false;
        using var httpClient = new HttpClient(new StubHandler((request, _) =>
        {
            requestSent = true;
            return ReleaseResponse(request, archive);
        }));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadAndInstallGeProtonAsync(CreateRelease()));

        Assert.False(requestSent);
        Assert.Empty(Directory.GetFileSystemEntries(
            Path.Combine(externalEmulators, "ProtonVersions")));
    }

    [Fact]
    public async Task DeleteManagedRunner_RejectsPathOutsideManagedRoot()
    {
        using var temp = new TemporaryDirectory();
        var externalDirectory = temp.CreateDirectory("ExternalRunner");
        temp.CreateFile("ExternalRunner/must-remain.txt");
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var removed = await service.DeleteManagedRunnerAsync(externalDirectory);

        Assert.False(removed);
        Assert.True(Directory.Exists(externalDirectory));
    }

    [Fact]
    public async Task DeleteManagedRunner_DeletesOnlyDirectManagedRunnerDirectory()
    {
        using var temp = new TemporaryDirectory();
        var managedRoot = temp.CreateDirectory("Emulators", "ProtonVersions");
        var runnerDirectory = temp.CreateDirectory("Emulators", "ProtonVersions", "GE-Proton10-1");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/proton");
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var rootRemoved = await service.DeleteManagedRunnerAsync("Emulators/ProtonVersions");
        var runnerRemoved = await service.DeleteManagedRunnerAsync(
            "Emulators/ProtonVersions/GE-Proton10-1/");

        Assert.False(rootRemoved);
        Assert.True(runnerRemoved);
        Assert.True(Directory.Exists(managedRoot));
        Assert.False(Directory.Exists(runnerDirectory));
    }

    [Fact]
    public async Task DeleteManagedRunner_RejectsSymbolicManagedRoot()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateDirectory("Emulators");
        var externalManagedRoot = temp.CreateDirectory("ExternalManagedRoot");
        var externalRunner = temp.CreateDirectory("ExternalManagedRoot", "GE-Proton10-1");
        var externalFile = temp.CreateFile(
            "ExternalManagedRoot/GE-Proton10-1/must-remain.txt");
        Directory.CreateSymbolicLink(
            temp.GetPath("Emulators", "ProtonVersions"),
            externalManagedRoot);
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var isManaged = service.IsManagedRunnerPath(
            "Emulators/ProtonVersions/GE-Proton10-1");
        var removed = await service.DeleteManagedRunnerAsync(
            "Emulators/ProtonVersions/GE-Proton10-1");

        Assert.False(isManaged);
        Assert.False(removed);
        Assert.True(Directory.Exists(externalRunner));
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public async Task DeleteManagedRunner_RejectsSymbolicRunnerDirectory()
    {
        using var temp = new TemporaryDirectory();
        var managedRoot = temp.CreateDirectory("Emulators", "ProtonVersions");
        var externalRunner = temp.CreateDirectory("ExternalRunner");
        var externalFile = temp.CreateFile("ExternalRunner/must-remain.txt");
        var runnerLink = Path.Combine(managedRoot, "GE-Proton10-1");
        Directory.CreateSymbolicLink(runnerLink, externalRunner);
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var isManaged = service.IsManagedRunnerPath(
            "Emulators/ProtonVersions/GE-Proton10-1");
        var removed = await service.DeleteManagedRunnerAsync(
            "Emulators/ProtonVersions/GE-Proton10-1");

        Assert.False(isManaged);
        Assert.False(removed);
        Assert.True(Directory.Exists(runnerLink));
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public async Task DeleteManagedRunner_DoesNotFollowNestedSymbolicLinks()
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory(
            "Emulators",
            "ProtonVersions",
            "GE-Proton10-1");
        temp.CreateFile("Emulators/ProtonVersions/GE-Proton10-1/proton");
        var externalDirectory = temp.CreateDirectory("ExternalData");
        var externalFile = temp.CreateFile("ExternalData/must-remain.txt");
        Directory.CreateSymbolicLink(
            Path.Combine(runnerDirectory, "external-link"),
            externalDirectory);
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var removed = await service.DeleteManagedRunnerAsync(
            "Emulators/ProtonVersions/GE-Proton10-1");

        Assert.True(removed);
        Assert.False(Directory.Exists(runnerDirectory));
        Assert.True(Directory.Exists(externalDirectory));
        Assert.True(File.Exists(externalFile));
    }

    [Fact]
    public void DetectRunnerKind_InspectsRunnerDirectory()
    {
        using var temp = new TemporaryDirectory();
        var wineDirectory = temp.CreateDirectory("CustomRunner");
        temp.CreateFile("CustomRunner/bin/wine");
        using var httpClient = new HttpClient(new StubHandler((_, _) =>
            throw new InvalidOperationException("HTTP should not be used.")));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var kind = service.DetectRunnerKind(wineDirectory);

        Assert.Equal(RunnerVersionKind.Wine, kind);
    }

    private static byte[] CreateTarGz(params (string Path, string Content)[] files)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: false))
        {
            foreach (var file in files)
            {
                var data = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Path)
                {
                    DataStream = data
                });
            }
        }

        return archive.ToArray();
    }

    private static GeProtonRelease CreateRelease(
        string downloadUrl = ReleaseDownloadUrl,
        string checksumUrl = ReleaseChecksumUrl) =>
        new(ReleaseTag, ReleaseAssetName, downloadUrl, checksumUrl);

    private static bool IsChecksumRequest(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.EndsWith(".sha512sum", StringComparison.Ordinal) == true;

    private static HttpResponseMessage ReleaseResponse(HttpRequestMessage request, byte[] archive) =>
        IsChecksumRequest(request) ? ChecksumResponse(archive) : BinaryResponse(archive);

    private static HttpResponseMessage ChecksumResponse(
        byte[] archive,
        string assetName = ReleaseAssetName)
    {
        var checksum = Convert.ToHexString(SHA512.HashData(archive)).ToLowerInvariant();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{checksum}  {assetName}\n", Encoding.UTF8, "text/plain")
        };
    }

    private static HttpResponseMessage RedirectResponse(string location) =>
        new(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri(location)
            }
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage BinaryResponse(byte[] content) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request, cancellationToken));
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
