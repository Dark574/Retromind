using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class ImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly ImportService _service = new();

    [Fact]
    public async Task ImportFromFolderAsync_UsesCueInsteadOfReferencedTrackFiles()
    {
        Directory.CreateDirectory(_root);
        WriteFile("Breath of Fire III (Track 1).bin");
        WriteFile("Breath of Fire III (Track 2).bin");
        WriteFile(
            "Breath of Fire III.cue",
            "FILE \"Breath of Fire III (Track 1).bin\" BINARY\n" +
            "FILE \"Breath of Fire III (Track 2).bin\" BINARY\n");
        WriteFile("Front Mission III.chd");
        WriteFile("Unreferenced Bonus.bin");

        var result = await _service.ImportFromFolderAsync(_root, ["cue", "bin", "chd"]);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, item => item.Title == "Breath of Fire III" &&
                                        item.Files.Count == 1 &&
                                        item.Files[0].Path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, item => item.Title == "Front Mission III");
        Assert.Contains(result, item => item.Title == "Unreferenced Bonus");
    }

    [Fact]
    public async Task ImportFromFolderAsync_GroupsMultiDiscCueFiles()
    {
        Directory.CreateDirectory(_root);
        for (var disc = 1; disc <= 3; disc++)
        {
            var baseName = $"Final Fantasy VII (Disc {disc})";
            WriteFile(baseName + ".bin");
            WriteFile(baseName + ".cue", $"FILE \"{baseName}.bin\" BINARY\n");
        }

        var result = await _service.ImportFromFolderAsync(_root, ["cue", "bin"]);

        var game = Assert.Single(result);
        Assert.Equal("Final Fantasy VII", game.Title);
        Assert.Equal([1, 2, 3], game.Files.Select(file => file.Index));
        Assert.All(game.Files, file =>
            Assert.EndsWith(".cue", file.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportFromFolderAsync_DoesNotSuppressPayloadWhenCueIsNotSelected()
    {
        Directory.CreateDirectory(_root);
        WriteFile("Game.bin");
        WriteFile("Game.cue", "FILE \"Game.bin\" BINARY\n");

        var result = await _service.ImportFromFolderAsync(_root, ["bin"]);

        var game = Assert.Single(result);
        Assert.EndsWith(".bin", game.Files[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteFile(string relativePath, string content = "data")
    {
        File.WriteAllText(Path.Combine(_root, relativePath), content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
