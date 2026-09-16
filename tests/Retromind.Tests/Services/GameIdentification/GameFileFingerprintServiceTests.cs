using Retromind.Services.GameIdentification;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.GameIdentification;

public sealed class GameFileFingerprintServiceTests
{
    private readonly GameFileFingerprintService _service = new();

    [Fact]
    public async Task CalculateAsync_KnownContent_ReturnsStandardChecksums()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("game.rom", "123456789");

        var result = await _service.CalculateAsync(filePath);

        Assert.Equal(9, result.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(filePath), result.LastWriteTimeUtc);
        Assert.Equal("cbf43926", result.Crc32);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", result.Md5);
        Assert.Equal("f7c3bc1d808e04732adf679965ccc34ca7ae3441", result.Sha1);
    }

    [Fact]
    public async Task CalculateAsync_EmptyFile_ReturnsStandardChecksums()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("empty.rom", string.Empty);

        var result = await _service.CalculateAsync(filePath);

        Assert.Equal(0, result.FileSize);
        Assert.Equal("00000000", result.Crc32);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", result.Md5);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", result.Sha1);
    }

    [Fact]
    public async Task CalculateAsync_MissingFile_ThrowsFileNotFoundException()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.GetPath("missing.rom");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _service.CalculateAsync(filePath));
    }

    [Fact]
    public async Task CalculateAsync_PreCanceledRequest_DoesNotOpenFile()
    {
        using var temp = new TemporaryDirectory();
        var filePath = temp.CreateFile("game.rom");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CalculateAsync(filePath, cancellation.Token));
    }
}
