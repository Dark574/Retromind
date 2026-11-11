using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Build;

public sealed class AppImageToolCacheTests : IDisposable
{
    private static readonly byte[] Elf = "\u007fELFtest packaging tool\n"u8.ToArray();
    private static readonly byte[] Runtime = "\u007fELFtest type2 runtime\n"u8.ToArray();
    private readonly AppImageScriptFixture _fixture = new();
    private readonly string _payload;
    private readonly string _checksum = Checksum(Elf);
    private readonly string _cache;

    public AppImageToolCacheTests()
    {
        _payload = _fixture.Files.GetPath("download");
        File.WriteAllBytes(_payload, Elf);
        _cache = _fixture.Files.GetPath("cache with spaces", _checksum, "appimagetool");
        _fixture.Environment["RETROMIND_TEST_DOWNLOAD"] = _payload;
        _fixture.Executable("curl", """
            destination= url=
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o) destination="$2"; shift 2 ;;
                https://*) url="$1"; shift ;;
                *) shift ;;
              esac
            done
            printf 'curl %s\n' "$url" >> "$RETROMIND_TEST_LOG"
            download="$RETROMIND_TEST_DOWNLOAD"
            case "$url" in
              */runtime-x86_64) download="${RETROMIND_TEST_RUNTIME_DOWNLOAD:-$download}" ;;
            esac
            cp "$download" "$destination"
            if [ -n "${RETROMIND_TEST_BARRIER:-}" ]; then
              touch "$RETROMIND_TEST_BARRIER/$$.ready"
              while [ ! -f "$RETROMIND_TEST_BARRIER/release" ]; do sleep 0.01; done
            fi
            exit "${RETROMIND_TEST_CURL_EXIT:-0}"
            """);
    }

    [Fact]
    public async Task DownloadThenOfflineReuse_RechecksBytesAndRestoresExecutePermission()
    {
        (await PrepareAsync()).AssertSuccess();
        Assert.Equal(Elf, File.ReadAllBytes(_cache));
        AssertExecutable(_cache);
        File.SetUnixFileMode(_cache, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = "22";

        var result = await PrepareAsync();

        result.AssertSuccess();
        Assert.Contains("Using verified cached", result.StandardOutput);
        AssertExecutable(_cache);
        Assert.Single(_fixture.Commands());
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task CorruptedCachedTool_IsReplacedByVerifiedDownload()
    {
        WriteCache("\u007fELFcorrupted cache"u8.ToArray());

        var result = await PrepareAsync();

        result.AssertSuccess();
        Assert.Contains("Ignoring invalid cached", result.StandardOutput);
        Assert.Equal(Elf, File.ReadAllBytes(_cache));
        AssertNoTemporaryDownloads();
    }

    [Theory]
    [InlineData("22", "Download failed")]
    [InlineData("0", "SHA-256 verification failed")]
    public async Task FailedOrWrongChecksumDownload_NeverReplacesExistingFile(string curlExit, string error)
    {
        var previous = "previous invalid cache entry"u8.ToArray();
        WriteCache(previous);
        File.WriteAllBytes(_payload, "\u007fELFpartial or changed upstream artifact"u8.ToArray());
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = curlExit;

        var result = await PrepareAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(error, result.StandardError);
        Assert.Equal(previous, File.ReadAllBytes(_cache));
        AssertNoTemporaryDownloads();
    }

    [Theory]
    [InlineData("<html>Gateway timeout</html>")]
    [InlineData("")]
    public async Task HtmlAndEmptyPayloads_AreRejectedEvenWithMatchingChecksum(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(_payload, data);

        var result = await PrepareAsync(checksum: Checksum(data));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not a valid ELF", result.StandardError);
        Assert.False(File.Exists(_cache));
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task NewPin_UsesDistinctEntryAndPreservesPreviousVerifiedVersion()
    {
        (await PrepareAsync()).AssertSuccess();
        byte[] updated = [.. Elf, .. "new version"u8];
        File.WriteAllBytes(_payload, updated);
        var checksum = Checksum(updated);
        var destination = _fixture.Files.GetPath("cache with spaces", checksum, "appimagetool");

        var result = await PrepareAsync(checksum, destination);

        result.AssertSuccess();
        Assert.Equal(updated, File.ReadAllBytes(destination));
        Assert.Equal(Elf, File.ReadAllBytes(_cache));
    }

    [Fact]
    public async Task OrphanedPartialDownload_IsNotUsedAsCachedTool()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        var orphan = _cache + ".tmp.abandoned";
        File.WriteAllBytes(orphan, Elf);
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = "22";

        var result = await PrepareAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(_cache));
        Assert.Equal(orphan, Assert.Single(_fixture.TemporaryDownloads()));
    }

    [Fact]
    public async Task ConcurrentCallers_PublishCompleteToolWithoutSharingTemporaryFile()
    {
        var barrier = PrepareBarrier();
        using var first = StartBlockedDownload();
        using var second = StartBlockedDownload();
        await WaitForDownloadsAsync(barrier, 2);
        Assert.Equal(2, _fixture.TemporaryDownloads().Length);
        Assert.False(File.Exists(_cache));

        File.WriteAllText(Path.Combine(barrier, "release"), "");
        var results = await Task.WhenAll(first.CompleteAsync(), second.CompleteAsync());

        foreach (var result in results)
            result.AssertSuccess();
        Assert.Equal(Elf, File.ReadAllBytes(_cache));
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task TerminatedDownload_LeavesNoPublishedToolOrTemporaryFile()
    {
        var barrier = PrepareBarrier();
        using var process = StartBlockedDownload();
        await WaitForDownloadsAsync(barrier, 1);

        await process.TerminateAsync();
        var result = await process.CompleteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(_cache));
        AssertNoTemporaryDownloads();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryOrSymlinkDestination_IsRejectedWithoutDownload(bool symlink)
    {
        var target = _fixture.Files.GetPath("unrelated");
        File.WriteAllBytes(target, Elf);
        var destination = _fixture.Files.GetPath("destination");
        if (symlink)
            File.CreateSymbolicLink(destination, target);
        else
            Directory.CreateDirectory(destination);

        var result = await PrepareAsync(destination: destination);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not a regular file", result.StandardError);
        Assert.False(File.Exists(_fixture.LogPath));
        Assert.Equal(Elf, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task DownloadFailure_StopsBeforeWorkCleanupOrAnyDockerCommand()
    {
        var (project, script) = PrepareBuild();
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = "22";

        var result = await _fixture.RunAsync(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Download failed", result.StandardError);
        Assert.True(File.Exists(Path.Combine(project, ".build-work", "previous-build")));
        Assert.StartsWith("curl ", Assert.Single(_fixture.Commands()));
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task RuntimeFailure_KeepsCachedPackagerAndPreviousWorkWithoutDocker()
    {
        var (project, script) = PrepareBuild();
        var tool = WriteBuildCache(project, "appimagetool", Elf);
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = "22";

        var result = await _fixture.RunAsync(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("continuous runtime may have changed", result.StandardOutput);
        Assert.Equal(Elf, File.ReadAllBytes(tool));
        Assert.True(File.Exists(Path.Combine(project, ".build-work", "previous-build")));
        Assert.NotEmpty(_fixture.Commands());
        Assert.All(_fixture.Commands(), command => Assert.StartsWith("curl ", command));
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task ColdBuild_VerifiesBothDownloadsBeforeFirstDockerCommand()
    {
        var (project, script) = PrepareBuild();

        var result = await _fixture.RunAsync(script);

        Assert.Equal(78, result.ExitCode);
        var commands = _fixture.Commands();
        Assert.True(commands.Length >= 3, result.StandardError);
        Assert.StartsWith("curl ", commands[0]);
        Assert.StartsWith("curl ", commands[1]);
        Assert.StartsWith("docker ", commands[2]);
        foreach (var (name, data) in new[] { ("appimagetool", Elf), ("runtime-x86_64", Runtime) })
        {
            var tool = BuildCachePath(project, name, data);
            Assert.Equal(data, File.ReadAllBytes(tool));
            AssertExecutable(tool);
        }
        Assert.False(File.Exists(Path.Combine(project, ".build-work", "previous-build")));
        AssertNoTemporaryDownloads();
    }

    [Fact]
    public async Task BothVerifiedCachedTools_SurviveWorkCleanupAndReachDockerOffline()
    {
        var (project, script) = PrepareBuild();
        var tools = new[] { WriteBuildCache(project, "appimagetool", Elf), WriteBuildCache(project, "runtime-x86_64", Runtime) };
        _fixture.Environment["RETROMIND_TEST_CURL_EXIT"] = "22";

        var result = await _fixture.RunAsync(script);

        // Stop at the fake build, without executing a container or packaging tool.
        Assert.Equal(78, result.ExitCode);
        Assert.Equal(2, Regex.Matches(result.StandardOutput, "Using verified cached").Count);
        Assert.All(tools, tool => Assert.True(File.Exists(tool)));
        Assert.False(File.Exists(Path.Combine(project, ".build-work", "previous-build")));
        var commands = File.ReadAllText(_fixture.LogPath);
        Assert.DoesNotContain("curl ", commands);
        Assert.Contains("docker buildx build ", commands);
        AssertNoTemporaryDownloads();
    }

    private Task<AppImageScriptFixture.ScriptResult> PrepareAsync(string? checksum = null, string? destination = null) =>
        _fixture.RunAsync(AppImageScriptFixture.ScriptPath("cache-appimage-tool.sh"),
            "test tool", "https://example.invalid/tool", checksum ?? _checksum, destination ?? _cache);

    private AppImageScriptFixture.ScriptProcess StartBlockedDownload() =>
        _fixture.Start(AppImageScriptFixture.ScriptPath("cache-appimage-tool.sh"),
            "test tool", "https://example.invalid/tool", _checksum, _cache);

    private void WriteCache(byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        File.WriteAllBytes(_cache, data);
    }

    private void AssertNoTemporaryDownloads() => Assert.Empty(_fixture.TemporaryDownloads());

    private static void AssertExecutable(string path) =>
        Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0, $"Not executable: {path}");

    private static string Checksum(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private string PrepareBarrier()
    {
        var barrier = _fixture.Files.CreateDirectory("barrier");
        _fixture.Environment["RETROMIND_TEST_BARRIER"] = barrier;
        return barrier;
    }

    private static async Task WaitForDownloadsAsync(string barrier, int count)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Directory.GetFiles(barrier, "*.ready").Length == count)
                return;
            await Task.Delay(10);
        }
        Assert.Fail("Download fixture did not reach the barrier.");
    }

    private (string Project, string Script) PrepareBuild()
    {
        var project = _fixture.Files.CreateDirectory("project with spaces");
        var build = Path.Combine(project, "build");
        _fixture.CopyBuildFiles(build, "legendary-version.env", "comet-version.env", "appimage-environment.env",
            "buildkitd.appimage.toml", "cache-appimage-tool.sh");
        // Keep production orchestration; replace only the artifact pins with tiny, non-executable fixture payloads.
        var script = File.ReadAllText(AppImageScriptFixture.ScriptPath("build-appimage.sh"));
        foreach (var (variable, checksum) in new[] { ("APPIMAGETOOL_SHA256", _checksum), ("APPIMAGE_RUNTIME_SHA256", Checksum(Runtime)) })
        {
            var pattern = "(?m)^" + variable + "=\"[0-9a-f]{64}\"$";
            Assert.Matches(pattern, script);
            script = Regex.Replace(script, pattern, variable + "=\"" + checksum + "\"");
        }
        var scriptPath = Path.Combine(build, "build-appimage.sh");
        File.WriteAllText(scriptPath, script);
        _fixture.Files.CreateFile("project with spaces/Retromind.csproj", "<InformationalVersion>0.0.0-test</InformationalVersion>\n");
        var runtime = _fixture.Files.GetPath("runtime-download");
        File.WriteAllBytes(runtime, Runtime);
        _fixture.Environment["RETROMIND_TEST_RUNTIME_DOWNLOAD"] = runtime;
        _fixture.Files.CreateFile("project with spaces/.build-work/previous-build", "preserve until tools are ready");
        _fixture.Executable("git", """
            case "$*" in
              *rev-parse*) printf 'test-commit\n' ;;
              *status*) ;;
              *) exit 98 ;;
            esac
            """);
        _fixture.Executable("docker", """
            printf 'docker %s\n' "$*" >> "$RETROMIND_TEST_LOG"
            case "$*" in
              'image inspect '*) printf 'test-image\n' ;;
              'buildx inspect '*) ;;
              'buildx build '*) exit 78 ;;
              'buildx prune '*) ;;
              *) exit 97 ;;
            esac
            """);
        return (project, scriptPath);
    }

    private static string BuildCachePath(string project, string name, byte[] data) =>
        Path.Combine(project, ".build-cache", "appimage-tools", Checksum(data), name);

    private static string WriteBuildCache(string project, string name, byte[] data)
    {
        var path = BuildCachePath(project, name, data);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose() => _fixture.Dispose();
}
