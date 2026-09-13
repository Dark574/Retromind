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

        Assert.True(Assert.Single(await service.GetBackupsAsync()).IsValid);
        await ChangeArchiveEntryAsync(created.FilePath, "retromind_tree.json", "[]");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadBackupAsync(created.FilePath));
    }

    [Theory]
    [InlineData("retromind_tree.json", "[]")]
    [InlineData("app_settings.json", "{}")]
    public async Task GetBackupsAsync_MarksChangedContentInvalid(string entryName, string changedJson)
    {
        using var temp = new TemporaryDirectory();
        var service = new MetadataBackupService(temp.RootPath);
        var created = await service.CreateBackupAsync(SampleContent, MetadataBackupReason.Manual);
        Assert.True(Assert.Single(await service.GetBackupsAsync()).IsValid);

        await ChangeArchiveEntryAsync(created.FilePath, entryName, changedJson);
        var listed = Assert.Single(await service.GetBackupsAsync());

        Assert.False(listed.IsValid);
        Assert.Contains(entryName, listed.ValidationError ?? string.Empty);
        Assert.True(File.Exists(created.FilePath));
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

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task CreateBackupAsync_RetainsTenValidAutomaticBackupsAndPreservesDamagedArchive(
        int damagedIndex)
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
        var service = new MetadataBackupService(temp.RootPath, () => now);
        var validPaths = new List<string>();
        string? damagedPath = null;

        // Damage an archive before later creations trigger retention. Cover both an
        // old damaged archive and one newer than most valid recovery points.
        for (var index = 0; index < 12; index++)
        {
            now = now.AddMinutes(1);
            var created = await service.CreateBackupAsync(SampleContent, MetadataBackupReason.OnStartup);
            if (index == damagedIndex)
            {
                damagedPath = created.FilePath;
                await ChangeArchiveEntryAsync(created.FilePath, "retromind_tree.json", "[]");
            }
            else
            {
                validPaths.Add(created.FilePath);
            }
        }

        var backups = await service.GetBackupsAsync();

        Assert.True(File.Exists(damagedPath));
        Assert.Equal(damagedPath, Assert.Single(backups, backup => !backup.IsValid).FilePath);
        Assert.Equal(
            validPaths.Skip(1).Order(StringComparer.Ordinal),
            backups.Where(backup => backup.IsValid).Select(backup => backup.FilePath).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(validPaths[0]));
    }

    private static async Task ChangeArchiveEntryAsync(string path, string entryName, string json)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        var changed = archive.CreateEntry(entryName);
        await using var writer = new StreamWriter(changed.Open());
        await writer.WriteAsync(json);
    }
}
