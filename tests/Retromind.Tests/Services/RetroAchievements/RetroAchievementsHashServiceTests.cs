using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsHashServiceTests
{
    private readonly RetroAchievementsHashService _service = new();

    [Fact]
    public async Task CalculateAsync_GameBoyFile_ReturnsOfficialWholeFileHash()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("game.gb", "123456789");

        var result = await _service.CalculateAsync("nintendo.game-boy", filePath);

        Assert.Equal("nintendo.game-boy", result.GameSystemId);
        Assert.Equal((uint)4, result.ConsoleId);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", result.Hash);
    }

    [Fact]
    public void NativeLibraryVersion_MatchesManagedBinding()
    {
        Assert.Equal(
            RetroAchievementsHashService.ExpectedNativeVersion,
            RetroAchievementsHashService.GetNativeVersion());
    }

    [Fact]
    public async Task CalculateAsync_UnsupportedSystem_DoesNotFallBackToRawMd5()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("game.adf", "123456789");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => _service.CalculateAsync("commodore.amiga", filePath));
    }

    [Fact]
    public async Task CalculateAsync_MissingFile_ThrowsFileNotFoundException()
    {
        using var temp = new TemporaryDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.CalculateAsync(
                "nintendo.game-boy",
                temp.GetPath("missing.gb")));
    }

    [Fact]
    public async Task CalculateAsync_InvalidChd_ReportsNativeReaderError()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("broken.chd", "not a CHD file");

        var exception = await Assert.ThrowsAsync<RetroAchievementsHashException>(
            () => _service.CalculateAsync("sony.playstation", filePath));

        Assert.Contains("Could not open CHD file", exception.Message);
    }

    [Theory]
    [InlineData("nintendo.snes", 3)]
    [InlineData("sony.playstation", 12)]
    [InlineData("sony.playstation-2", 21)]
    [InlineData("nintendo.nds", 18)]
    [InlineData("arcade", 27)]
    public void ConsoleCatalog_UsesOfficialRcheevosIdentifiers(
        string gameSystemId,
        uint expectedConsoleId)
    {
        var found = RetroAchievementsConsoleCatalog.TryGetConsoleId(
            gameSystemId,
            out var consoleId);

        Assert.True(found);
        Assert.Equal(expectedConsoleId, consoleId);
    }
}
