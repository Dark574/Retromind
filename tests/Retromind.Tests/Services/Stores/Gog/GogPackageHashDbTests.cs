using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPackageHashDbTests
{
    private const string HashDbName = "goggame-123.hashdb";

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\file")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("a/../file")]
    [InlineData("a\\..\\file")]
    [InlineData("a/file:stream")]
    [InlineData("a//file")]
    [InlineData("a/.. /file")]
    public void Read_RejectsRootedOrEscapingPaths(string path)
    {
        using var data = new MemoryStream(BuildHashDb(HashDbName, (path, "data")));
        Assert.Throws<InvalidDataException>(() => GogPackageHashDb.Read(data, HashDbName));
    }

    [Fact]
    public void Read_OnlyAcceptsLegacyRootMarkerWhenBackedByPackageInventory()
    {
        var bytes = BuildHashDb(HashDbName, ("/win32/game.exe", "data"));
        using var mismatched = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => GogPackageHashDb.Read(mismatched, HashDbName,
            new Dictionary<string, long> { ["other/game.exe"] = 4 }, allowArchiveRootMarker: true));

        using var matched = new MemoryStream(bytes);
        var manifest = GogPackageHashDb.Read(matched, HashDbName,
            new Dictionary<string, long> { ["win32/game.exe"] = 4 }, allowArchiveRootMarker: true);
        Assert.Equal(1, manifest.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_RejectsSymbolicLinksInTargetPath(bool linkIsDirectory)
    {
        using var temp = new TemporaryDirectory();
        var destination = temp.CreateDirectory("target");
        var outside = temp.CreateFile("outside/file", "data");
        if (linkIsDirectory)
        {
            Directory.CreateSymbolicLink(Path.Combine(destination, "sub"), Path.GetDirectoryName(outside)!);
        }
        else
        {
            var subfolder = Directory.CreateDirectory(Path.Combine(destination, "sub"));
            File.CreateSymbolicLink(Path.Combine(subfolder.FullName, "file"), outside);
        }

        using var data = new MemoryStream(BuildHashDb(HashDbName, ("sub/file", "data")));
        var manifest = GogPackageHashDb.Read(data, HashDbName);

        var error = await manifest.ValidateAsync(destination, manifest.CaptureExistingFiles(destination, default),
            verifyAllHashes: true, default);

        Assert.Contains("sub/file", error);
        Assert.Equal("data", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Verify_OptionalMd5FindsChangedContentWithIdenticalLength()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("game.exe", "bad!");
        using var data = new MemoryStream(BuildHashDb(HashDbName, ("game.exe", "good")));
        var manifest = GogPackageHashDb.Read(data, HashDbName,
            new Dictionary<string, long> { ["game.exe"] = 4 });
        var before = new Dictionary<string, GogPackageHashDb.FileStamp>();

        Assert.Null(await manifest.ValidateAsync(temp.RootPath, before, verifyAllHashes: false, default));
        Assert.Contains("wrong MD5", await manifest.ValidateAsync(temp.RootPath, before, verifyAllHashes: true, default));

        var existingFiles = manifest.CaptureExistingFiles(temp.RootPath, default);
        Assert.Contains("wrong MD5", await manifest.ValidateAsync(temp.RootPath, existingFiles, verifyAllHashes: false, default));
        File.WriteAllText(path, "shorter");
        Assert.Contains("wrong size", await manifest.ValidateAsync(temp.RootPath, before, verifyAllHashes: false, default));
    }

    internal static byte[] BuildHashDb(string name, params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new BinaryWriter(zip.CreateEntry(name + ".temporary").Open()))
        {
            writer.Write(12);
            writer.Write(1);
            writer.Write(entries.Length);
            foreach (var (path, content) in entries)
            {
                var pathBytes = Encoding.UTF8.GetBytes(path);
                writer.Write(pathBytes);
                writer.Write(new byte[1024 - pathBytes.Length]);
                writer.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content)))));
            }
        }

        return output.ToArray();
    }
}
