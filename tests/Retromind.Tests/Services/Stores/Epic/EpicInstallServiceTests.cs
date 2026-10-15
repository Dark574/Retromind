using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class EpicInstallServiceTests
{
    [Fact]
    public void AssessDirectory_AllowsNewAndEmptyDirectories()
    {
        using var temp = new TemporaryDirectory();
        var service = CreateService();
        var item = CreateEpicItem();
        var emptyPath = temp.CreateDirectory("empty");

        var missing = service.AssessDirectory(temp.GetPath("new-game"), item);
        var empty = service.AssessDirectory(emptyPath, item);

        Assert.Equal(EpicInstallDirectoryStatus.NewDirectory, missing.Status);
        Assert.True(missing.IsAllowed);
        Assert.Equal(EpicInstallDirectoryStatus.EmptyDirectory, empty.Status);
        Assert.True(empty.IsAllowed);
    }

    [Fact]
    public void AssessDirectory_RejectsNonEmptyDirectoryWithoutMatchingMarker()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("existing-game");
        File.WriteAllText(Path.Combine(installPath, "unrelated.txt"), "keep me");
        var service = CreateService();

        var assessment = service.AssessDirectory(installPath, CreateEpicItem());

        Assert.Equal(EpicInstallDirectoryStatus.UnownedDirectory, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void WriteMarker_AllowsSameItemToResumePartialInstallation()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.GetPath("partial-game");
        var service = CreateService();
        var item = CreateEpicItem();

        service.WriteMarker(installPath, item);
        File.WriteAllText(Path.Combine(installPath, "partial.chunk"), "partial");
        var assessment = service.AssessDirectory(installPath, item);

        Assert.Equal(EpicInstallDirectoryStatus.OwnedDirectory, assessment.Status);
        Assert.True(assessment.IsAllowed);
    }

    private static EpicInstallService CreateService() =>
        new(new LegendaryCliClient(new UnusedProcessRunner()));

    private static MediaItem CreateEpicItem() => new("Example Game")
    {
        CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic,
            [CustomFieldKeyHelper.StoreGameId] = "ExampleGame"
        }
    };

    private sealed class UnusedProcessRunner : ILegendaryCliProcessRunner
    {
        public Task<LegendaryCliResult> RunAsync(
            LegendaryCliCommand command,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("No Legendary command expected.");
    }
}
