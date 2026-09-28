using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class RunnerVersionServiceTests
{
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
                    "browser_download_url": "https://example.invalid/ge-proton.tar.gz"
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
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_PublishesOnlyValidatedRunner()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var relativePath = await service.DownloadAndInstallGeProtonAsync(
            new GeProtonRelease(
                "GE-Proton10-1",
                "GE-Proton10-1.tar.gz",
                "https://example.invalid/ge-proton.tar.gz"));

        Assert.Equal("Emulators/ProtonVersions/GE-Proton10-1", relativePath);
        Assert.True(File.Exists(temp.GetPath(relativePath, "proton")));
        Assert.True(File.Exists(temp.GetPath(relativePath, "toolmanifest.vdf")));
        Assert.Empty(Directory.GetDirectories(
            temp.GetPath("Emulators", "ProtonVersions"),
            ".tmp_ge_*"));
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
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(
            new GeProtonRelease(
                "GE-Proton10-1",
                "GE-Proton10-1.tar.gz",
                "https://example.invalid/ge-proton.tar.gz"),
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
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(
            new GeProtonRelease(
                "GE-Proton10-1",
                "GE-Proton10-1.tar.gz",
                "https://example.invalid/ge-proton.tar.gz"));

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
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndInstallGeProtonAsync(
                new GeProtonRelease(
                    "GE-Proton10-1",
                    "GE-Proton10-1.tar.gz",
                    "https://example.invalid/ge-proton.tar.gz"),
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
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await service.DownloadAndInstallGeProtonAsync(
            new GeProtonRelease(
                "GE-Proton10-1",
                "GE-Proton10-1.tar.gz",
                "https://example.invalid/ge-proton.tar.gz"),
            progress: new InlineProgress<GeProtonInstallProgress>(reports.Add));

        Assert.Contains(reports, report =>
            report.Stage == GeProtonInstallStage.Downloading &&
            report.DownloadedBytes == archive.Length &&
            report.TotalBytes == archive.Length &&
            report.Percentage == 100d);
        Assert.Contains(reports, report => report.Stage == GeProtonInstallStage.Installing);
    }

    [Fact]
    public async Task DownloadAndInstallGeProton_CanceledDownloadLeavesNoPublishedRunner()
    {
        var archive = CreateTarGz(
            ("GE-Proton10-1/proton", "runner"),
            ("GE-Proton10-1/toolmanifest.vdf", "manifest"));
        using var cancellation = new CancellationTokenSource();

        using var temp = new TemporaryDirectory();
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        var progress = new InlineProgress<GeProtonInstallProgress>(report =>
        {
            if (report.Stage == GeProtonInstallStage.Downloading)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAndInstallGeProtonAsync(
                new GeProtonRelease(
                    "GE-Proton10-1",
                    "GE-Proton10-1.tar.gz",
                    "https://example.invalid/ge-proton.tar.gz"),
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
        using var httpClient = new HttpClient(new StubHandler((_, _) => BinaryResponse(archive)));
        using var service = new RunnerVersionService(temp.RootPath, httpClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndInstallGeProtonAsync(
                new GeProtonRelease(
                    "GE-Proton10-1",
                    "GE-Proton10-1.tar.gz",
                    "https://example.invalid/ge-proton.tar.gz")));

        var runnerRoot = temp.GetPath("Emulators", "ProtonVersions");
        Assert.False(Directory.Exists(Path.Combine(runnerRoot, "GE-Proton10-1")));
        Assert.Empty(Directory.GetDirectories(runnerRoot, ".tmp_ge_*"));
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
