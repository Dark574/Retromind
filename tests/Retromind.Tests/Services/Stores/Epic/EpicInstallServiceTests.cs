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

    [Fact]
    public void WriteMarker_RejectsDirectoryThatChangedAfterAssessment()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("changed-game");
        var service = CreateService();
        var item = CreateEpicItem();
        var initialAssessment = service.AssessDirectory(installPath, item);
        Assert.Equal(EpicInstallDirectoryStatus.EmptyDirectory, initialAssessment.Status);

        File.WriteAllText(Path.Combine(installPath, "foreign-file.txt"), "not managed by Retromind");

        Assert.Throws<InvalidOperationException>(() => service.WriteMarker(installPath, item));
        Assert.False(File.Exists(Path.Combine(installPath, ".retromind-install.json")));
    }

    [Fact]
    public async Task InstallAsync_RechecksDirectoryOwnershipBeforeCallingLegendary()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("changed-before-install");
        var runner = new QueueProcessRunner();
        var service = CreateService(runner);
        var item = CreateEpicItem();
        service.WriteMarker(installPath, item);
        File.Delete(Path.Combine(installPath, ".retromind-install.json"));
        File.WriteAllText(Path.Combine(installPath, "foreign-file.txt"), "not managed by Retromind");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallAsync(item, installPath, outputReceived: null));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task UninstallAsync_RemovesLegendaryRegistrationAndOwnedInstallDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("installed-game");
        var item = CreateEpicItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = installPath;
        var runner = new QueueProcessRunner(
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty));
        var service = CreateService(runner);
        service.WriteMarker(installPath, item);
        File.WriteAllText(Path.Combine(installPath, "game.dat"), "game data");

        await service.UninstallAsync(item);

        Assert.False(Directory.Exists(installPath));
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(
                ["--yes", "uninstall", "ExampleGame", "--skip-uninstaller"],
                command.Arguments),
            command => Assert.Equal(["list-installed", "--json"], command.Arguments));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UninstallAsync_DeletesPrefixUnlessItMustBePreserved(
        bool deletePrefix,
        bool expectedPrefixExists)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var installPath = temp.CreateDirectory("Library", "Games", "Epic", "Example Game");
        var prefixPath = temp.CreateDirectory("Library", "Prefixes", "epic_ExampleGame");
        File.WriteAllText(Path.Combine(prefixPath, "prefix.dat"), "prefix data");
        var item = CreateEpicItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] =
            Path.GetRelativePath(AppPaths.DataRoot, installPath);
        item.PrefixPath = Path.GetRelativePath(AppPaths.LibraryRoot, prefixPath);
        var runner = new QueueProcessRunner(
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty));
        var service = CreateService(runner);
        service.WriteMarker(installPath, item);

        await service.UninstallAsync(item, deletePrefix);

        Assert.Equal(expectedPrefixExists, Directory.Exists(prefixPath));
    }

    private static EpicInstallService CreateService() =>
        new(new LegendaryCliClient(new UnusedProcessRunner()));

    private static EpicInstallService CreateService(ILegendaryCliProcessRunner runner) =>
        new(new LegendaryCliClient(runner));

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

    private sealed class QueueProcessRunner(params LegendaryCliResult[] results) : ILegendaryCliProcessRunner
    {
        private readonly Queue<LegendaryCliResult> _results = new(results);

        internal List<LegendaryCliCommand> Commands { get; } = [];

        public Task<LegendaryCliResult> RunAsync(
            LegendaryCliCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
