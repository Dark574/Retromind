using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallSpaceServiceTests
{
    [Fact]
    public void Assess_BlocksWhenRemainingDownloadAndReserveDoNotFit()
    {
        var package = CreatePackage(2_000);
        var available = GogInstallSpaceService.FreeSpaceReserveBytes + 1_999;

        var result = GogInstallSpaceService.Assess(package, "missing", "/games", available);

        Assert.Equal(GogInstallSpaceStatus.InsufficientForDownload, result.Status);
        Assert.Equal(2_000, result.RemainingDownloadBytes);
    }

    [Fact]
    public void Assess_WarnsWhenDownloadFitsButEstimatedInstallationDoesNot()
    {
        var package = CreatePackage(2_000);
        var available = GogInstallSpaceService.FreeSpaceReserveBytes + 2_000;

        var result = GogInstallSpaceService.Assess(package, "missing", "/games", available);

        Assert.Equal(GogInstallSpaceStatus.EstimatedInstallMayNotFit, result.Status);
        Assert.Equal(GogInstallSpaceService.FreeSpaceReserveBytes + 4_000, result.EstimatedPeakAdditionalBytes);
    }

    [Fact]
    public void Assess_CreditsExistingPartialOrCompleteStagingBytesOnlyOnce()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stagingDirectory = temporaryDirectory.CreateDirectory("staging");
        SetFileLength(Path.Combine(stagingDirectory, "setup.bin"), 200);
        SetFileLength(Path.Combine(stagingDirectory, "setup.bin.part"), 600);
        var package = CreatePackage(1_000);

        var result = GogInstallSpaceService.Assess(
            package,
            stagingDirectory,
            temporaryDirectory.RootPath,
            GogInstallSpaceService.FreeSpaceReserveBytes + 2_000);

        Assert.Equal(400, result.RemainingDownloadBytes);
        Assert.Equal(GogInstallSpaceStatus.Sufficient, result.Status);
    }

    [Fact]
    public void Assess_ReportsUnknownWhenAnyFileSizeIsMissing()
    {
        var package = new GogInstallerPackage(
            "123",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile("https://example.invalid/setup.bin", "setup.bin", null)]);

        var result = GogInstallSpaceService.Assess(package, "missing", "/games", long.MaxValue);

        Assert.Equal(GogInstallSpaceStatus.PackageSizeUnknown, result.Status);
        Assert.Null(result.InstallerBytes);
    }

    private static GogInstallerPackage CreatePackage(long size) =>
        new(
            "123",
            GogInstallPlatform.Linux,
            "Installer",
            "1.0",
            [new GogInstallerDownloadFile("https://example.invalid/setup.bin", "setup.bin", size)]);

    private static void SetFileLength(string path, long length)
    {
        using var stream = File.Create(path);
        stream.SetLength(length);
    }
}
