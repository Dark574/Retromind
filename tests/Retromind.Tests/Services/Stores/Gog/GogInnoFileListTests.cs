using System.Security.Cryptography;
using System.Text;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInnoFileListTests
{
    [Fact]
    public async Task Validate_RecognizesLanguageAlternativesAndChecksOnlyGameFiles()
    {
        using var temp = new TemporaryDirectory();
        var install = temp.CreateDirectory("game");
        temp.CreateFile("game/galaxy.exe", "payload");
        temp.CreateFile("game/goggame-123.info", "English");
        temp.CreateFile("game/webcache.zip", "cache");
        temp.CreateFile("prefix/GOG.com/supportInstaller/uninstall.dll", "unrelated");
        temp.CreateFile("game/unins000.exe", "unrelated");

        var list = new GogInnoFileList();
        var shared = new[]
        {
            Entry(@"galaxy.exe", "payload"),
            Entry(@"{app}\webcache.zip", "cache", useSha1: true),
            Entry(@"{tmp}\unpack.exe", "script"),
            Entry(@"{commonappdata}\GOG.com\supportInstaller\uninstall.dll", "different"),
            Entry(@"{app}\unins000.exe", "different"),
            Entry(@"__redist\redist.exe", "dependency")
        };
        Add(list, [.. shared, Entry("goggame-123.info", "German")]);
        Add(list, [.. shared, Entry("goggame-123.info", "English")]);

        var result = await list.ValidateAsync(install, @"Z:\Game", temp.GetPath("setup.log"), default);

        Assert.True(result.Complete);
        Assert.Equal(3, result.Verified);
    }

    [Fact]
    public async Task Validate_IgnoresGogSupportHelperButStillRequiresGameSupportFiles()
    {
        using var temp = new TemporaryDirectory();
        var install = temp.CreateDirectory("game");
        var map = temp.CreateFile("game/SUPPORT/Campaign Map.bmp", "map");
        var list = new GogInnoFileList();
        Add(list, Entry(@"{app}\__support\scriptinterpreter.exe", "installer helper"),
            Entry(@"{app}\SUPPORT\Campaign Map.bmp", "map"));
        var log = temp.CreateFile("setup.log", """
            2026-11-25 16:55:43.364   -- File entry --
            2026-11-25 16:55:43.364   Dest filename: Z:\Game\__support\scriptinterpreter.exe
            2026-11-25 16:55:43.375   Successfully installed the file.
            2026-11-25 16:56:28.879   -- File entry --
            2026-11-25 16:56:28.879   Dest filename: Z:\Game\SUPPORT\Campaign Map.bmp
            2026-11-25 16:56:28.921   Successfully installed the file.
            """);

        var result = await list.ValidateAsync(install, @"Z:\Game", log, default);

        Assert.True(result.Complete, result.Failure);
        Assert.Equal(1, result.Verified);

        File.Delete(map);
        result = await list.ValidateAsync(install, @"Z:\Game", log, default);
        Assert.Contains("Required package file is missing: 'SUPPORT/Campaign Map.bmp'", result.Failure);
    }

    [Fact]
    public async Task Validate_LeavesUnconfirmedLanguageFilesAndComponentsUnverified()
    {
        using var temp = new TemporaryDirectory();
        var install = temp.CreateDirectory("game");
        temp.CreateFile("game/game.exe", "game");
        temp.CreateFile("game/optional.dat", "old!");
        var list = new GogInnoFileList();
        Add(list, Entry(@"{app}\game.exe", "game"), Entry(@"video-fr.dat", "video"));
        Add(list, Entry(@"{app}\game.exe", "game"), Entry(@"video-en.dat", "video"),
            Entry(@"optional.dat", "data"));
        // A destination mention alone does not establish selection, and paths outside the
        // chosen game folder cannot establish it even when the setup installed them.
        var log = temp.CreateFile("setup.log", """
            2026-11-25 12:14:27.834   -- File entry --
            2026-11-25 12:14:27.834   Dest filename: Z:\Game\optional.dat
            2026-11-25 12:14:27.834   Skipping file.
            2026-11-25 12:14:27.834   -- File entry --
            2026-11-25 12:14:27.834   Dest filename: Z:\Games\video-en.dat
            2026-11-25 12:14:27.834   Successfully installed the file.
            """);

        var result = await list.ValidateAsync(install, @"Z:\Game", log, default);

        Assert.Null(result.Failure);
        Assert.False(result.Complete);
        Assert.Equal(1, result.Verified);
        Assert.Equal(4, result.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validate_DoesNotReadFilesThroughSymlinks(bool directory)
    {
        using var temp = new TemporaryDirectory();
        var install = temp.CreateDirectory("game");
        var file = temp.CreateFile("outside/file.dat", "same data");
        var subPath = Path.Combine(install, "sub");
        if (directory)
            Directory.CreateSymbolicLink(subPath, Path.GetDirectoryName(file)!);
        else
        {
            Directory.CreateDirectory(subPath);
            File.CreateSymbolicLink(Path.Combine(subPath, "file.dat"), file);
        }

        var list = new GogInnoFileList();
        Add(list, Entry(@"{app}\sub\file.dat", "same data"));
        var log = temp.CreateFile("setup.log", """
            2026-11-25 12:14:27.834   -- File entry --
            2026-11-25 12:14:27.834   Dest filename: Z:\Game\sub\file.dat
            2026-11-25 12:14:27.834   Successfully installed the file.
            """);

        var result = await list.ValidateAsync(install, @"Z:\Game", log, default);

        Assert.Null(result.Failure);
        Assert.Equal(0, result.Verified);
        Assert.False(result.Complete);
    }

    [Theory]
    [InlineData(@"{app}\..\file")]
    [InlineData(@"Z:\file")]
    [InlineData(@"\\unc\file")]
    [InlineData(@"folder\item:stream")]
    public void Read_RejectsUnsafeDestinations(string path)
    {
        var list = new GogInnoFileList();
        Assert.Throws<InvalidDataException>(() => Add(list, Entry(path, "data")));
    }

    [Fact]
    public void Read_RejectsInvalidChecksumsAndDuplicateDestinations()
    {
        Assert.Throws<InvalidDataException>(() => Add(new(), "4 MD5 incorrect game.exe"));
        Assert.Throws<InvalidDataException>(() => Add(new(),
            Entry(@"Data\game.exe", "game"), Entry(@"{app}\data\Game.exe", "game")));
    }

    private static string Entry(string path, string content, bool useSha1 = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var digest = useSha1 ? SHA1.HashData(bytes) : MD5.HashData(bytes);
        return $"{bytes.Length} {(useSha1 ? "SHA-1" : "MD5")} {Convert.ToHexString(digest)} {path}";
    }

    private static void Add(GogInnoFileList list, params string[] entries)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
        list.AddLanguage(reader, "goggame-123.hashdb");
    }
}
