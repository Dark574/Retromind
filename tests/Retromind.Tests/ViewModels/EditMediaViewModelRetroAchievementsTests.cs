using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class EditMediaViewModelRetroAchievementsTests
{
    private const string KnownHash = "25f9e794323b453885f5181f1b624d0b";

    [Fact]
    public async Task IdentifyCommand_StagesMatchUntilEditorIsSaved()
    {
        using var temp = new TemporaryDirectory();
        var gamePath = temp.CreateFile("game.gb");
        var item = CreateItem(gamePath);
        var parent = new MediaNode
        {
            Name = "Game Boy",
            GameSystemId = "nintendo.game-boy"
        };
        parent.Items.Add(item);
        var identificationService = new StubIdentificationService(
            new RetroAchievementsIdentificationResult(
                new RetroAchievementsGameHash("nintendo.game-boy", 4, KnownHash),
                new RetroAchievementsGameCatalogEntry
                {
                    GameId = 123,
                    Title = "Test Game",
                    ConsoleId = 4,
                    ConsoleName = "Game Boy",
                    Hashes = [KnownHash]
                }));
        using var viewModel = CreateViewModel(temp, item, parent, identificationService);

        await viewModel.IdentifyRetroAchievementsCommand.ExecuteAsync(null);

        Assert.Null(item.RetroAchievementsGame);
        Assert.Equal("nintendo.game-boy", identificationService.LastGameSystemId);
        Assert.Equal(gamePath, identificationService.LastFilePath);

        viewModel.SaveAndCloseCommand.Execute(null);

        Assert.NotNull(item.RetroAchievementsGame);
        Assert.Equal(123, item.RetroAchievementsGame.GameId);
        Assert.Equal(4u, item.RetroAchievementsGame.ConsoleId);
        Assert.Equal(KnownHash, item.RetroAchievementsGame.Hash);
        Assert.Equal("Test Game", item.RetroAchievementsGame.Title);
    }

    [Fact]
    public void ChangingGameSystemClearsExistingIdentityWhenEditorIsSaved()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateItem(temp.CreateFile("game.gb"));
        item.RetroAchievementsGame = new RetroAchievementsGameIdentity
        {
            GameId = 123,
            ConsoleId = 4,
            GameSystemId = "nintendo.game-boy",
            Hash = KnownHash,
            Title = "Test Game"
        };
        var parent = new MediaNode
        {
            Name = "Game Boy",
            GameSystemId = "nintendo.game-boy"
        };
        parent.Items.Add(item);
        using var viewModel = CreateViewModel(
            temp,
            item,
            parent,
            new StubIdentificationService(null));

        viewModel.SelectedGameSystem = Assert.Single(
            viewModel.AvailableGameSystems,
            option => option.Id == "nintendo.snes");
        viewModel.SaveAndCloseCommand.Execute(null);

        Assert.Null(item.RetroAchievementsGame);
        Assert.Equal("nintendo.snes", item.GameSystemId);
    }

    private static MediaItem CreateItem(string gamePath)
    {
        return new MediaItem
        {
            Title = "Test Game",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = gamePath,
                    Index = 1
                }
            ]
        };
    }

    private static EditMediaViewModel CreateViewModel(
        TemporaryDirectory temp,
        MediaItem item,
        MediaNode parent,
        IRetroAchievementsGameIdentificationService identificationService)
    {
        var settings = new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = true,
                Username = "TestUser"
            }
        };
        var roots = new ObservableCollection<MediaNode> { parent };
        return new EditMediaViewModel(
            item,
            settings,
            new FileManagementService(temp.CreateDirectory("Library")),
            [parent.Name],
            identificationService,
            rootNodes: roots,
            parentNode: parent);
    }

    private sealed class StubIdentificationService(RetroAchievementsIdentificationResult? result)
        : IRetroAchievementsGameIdentificationService
    {
        public string? LastGameSystemId { get; private set; }
        public string? LastFilePath { get; private set; }

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastGameSystemId = gameSystemId;
            LastFilePath = filePath;
            return Task.FromResult(result ?? throw new InvalidOperationException("Unexpected call."));
        }
    }
}
