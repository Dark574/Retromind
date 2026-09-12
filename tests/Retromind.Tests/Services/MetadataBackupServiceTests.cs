using System.IO.Compression;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class MetadataBackupServiceTests
{
    private static readonly MetadataBackupContent SampleContent = new(
        "[{\"Id\":\"root\",\"Name\":\"Games\"}]",
        "{\"ItemWidth\":180}");

    [Fact]
    public async Task CreateBackupAsync_CreatesValidatedRoundTripArchive()
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 12, 8, 30, 15, TimeSpan.Zero);
        var service = new MetadataBackupService(temp.RootPath, () => now);

        var created = await service.CreateBackupAsync(SampleContent, MetadataBackupReason.Manual);
        var listed = Assert.Single(await service.GetBackupsAsync());
        var restored = await service.ReadBackupAsync(created.FilePath);

        Assert.True(created.IsValid);
        Assert.Equal(now, created.CreatedUtc);
        Assert.Equal(MetadataBackupReason.Manual, listed.Reason);
        Assert.Equal(SampleContent, restored);
        Assert.True(File.Exists(created.FilePath));

        using var archive = ZipFile.OpenRead(created.FilePath);
        Assert.Equal(
            ["app_settings.json", "backup-manifest.json", "retromind_tree.json"],
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadBackupAsync_RejectsChangedContent()
    {
        using var temp = new TemporaryDirectory();
        var service = new MetadataBackupService(temp.RootPath);
        var created = await service.CreateBackupAsync(SampleContent, MetadataBackupReason.Manual);

        using (var archive = ZipFile.Open(created.FilePath, ZipArchiveMode.Update))
        {
            archive.GetEntry("retromind_tree.json")!.Delete();
            var changed = archive.CreateEntry("retromind_tree.json");
            await using var writer = new StreamWriter(changed.Open());
            await writer.WriteAsync("[]");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadBackupAsync(created.FilePath));
    }

    [Fact]
    public async Task GetBackupsAsync_MarksUnreadableArchiveInvalid()
    {
        using var temp = new TemporaryDirectory();
        var service = new MetadataBackupService(temp.RootPath);
        Directory.CreateDirectory(service.BackupDirectory);
        File.WriteAllText(
            Path.Combine(service.BackupDirectory, "RetromindMetadata-invalid.zip"),
            "not a zip archive");

        var listed = Assert.Single(await service.GetBackupsAsync());

        Assert.False(listed.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(listed.ValidationError));
    }

    [Fact]
    public async Task CreateBackupAsync_PrunesCombinedAutomaticBackupsButKeepsManualAndPreRestore()
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
        var service = new MetadataBackupService(temp.RootPath, () => now);

        await service.CreateBackupAsync(SampleContent, MetadataBackupReason.Manual);
        now = now.AddMinutes(1);
        await service.CreateBackupAsync(SampleContent, MetadataBackupReason.BeforeRestore);
        var automaticReasons = new[]
        {
            MetadataBackupReason.BeforeBulkEdit,
            MetadataBackupReason.BeforeBulkScrape,
            MetadataBackupReason.OnStartup
        };
        for (var index = 0; index < 12; index++)
        {
            now = now.AddMinutes(1);
            await service.CreateBackupAsync(SampleContent, automaticReasons[index % automaticReasons.Length]);
        }

        var backups = await service.GetBackupsAsync();
        Assert.Equal(12, backups.Count);
        Assert.Single(backups, backup => backup.Reason == MetadataBackupReason.Manual);
        Assert.Single(backups, backup => backup.Reason == MetadataBackupReason.BeforeRestore);
        Assert.Equal(
            10,
            backups.Count(backup => backup.Reason is MetadataBackupReason.BeforeBulkEdit or
                MetadataBackupReason.BeforeBulkScrape or MetadataBackupReason.OnStartup));
        Assert.DoesNotContain(backups, backup => backup.CreatedUtc == new DateTimeOffset(2026, 9, 12, 8, 2, 0, TimeSpan.Zero));
        Assert.DoesNotContain(backups, backup => backup.CreatedUtc == new DateTimeOffset(2026, 9, 12, 8, 3, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task DeleteBackupAsync_RejectsPathOutsideManagedBackupDirectory()
    {
        using var temp = new TemporaryDirectory();
        var service = new MetadataBackupService(temp.RootPath);
        var outsidePath = temp.CreateFile("RetromindMetadata-outside.zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteBackupAsync(outsidePath));
        Assert.True(File.Exists(outsidePath));
    }
}
