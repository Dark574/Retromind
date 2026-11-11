using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Build;

public sealed class AppImageEnvironmentTests : IDisposable
{
    private readonly AppImageScriptFixture _fixture = new();

    [Theory]
    [InlineData(null)]
    [InlineData("noninteractive")]
    [InlineData("dialog")]
    public async Task FixedVersions_PassWithRetriesDisabledBeforeAnyAptRequest(string? frontend)
    {
        PrepareApt();
        if (frontend is not null)
            _fixture.Environment["DEBIAN_FRONTEND"] = frontend;

        var result = await UpgradeAsync();

        result.AssertSuccess();
        Assert.Equal(new[]
        {
            $"{frontend ?? ""}|-o Acquire::Retries=0 -o APT::Update::Error-Mode=any update",
            "noninteractive|-o Acquire::Retries=0 upgrade -y"
        }, _fixture.Commands());
        Assert.Contains("Validated apt=2.6.2", result.StandardOutput);
        Assert.Contains("Validated libapt-pkg6.0=2.6.2", result.StandardOutput);
    }

    [Theory]
    [InlineData("2.6.1", "2.6.2")]
    [InlineData("2.6.2", "2.6.1")]
    [InlineData("2.6.2~rc1", "2.6.2")]
    public async Task OldAptOrLibraryAndPrerelease_AreRejected(string apt, string library)
    {
        PrepareApt(apt, library);

        var result = await UpgradeAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("APT security update missing", result.StandardError);
    }

    [Fact]
    public async Task NewerSecurityRevision_IsAccepted()
    {
        PrepareApt("2.6.2+deb12u1", "2.6.3");

        (await UpgradeAsync()).AssertSuccess();
    }

    [Fact]
    public async Task FailedIndexUpdate_StopsBeforeUpgrade()
    {
        PrepareApt();
        _fixture.Environment["RETROMIND_TEST_UPDATE_EXIT"] = "42";

        var result = await UpgradeAsync();

        Assert.Equal(42, result.ExitCode);
        Assert.Single(_fixture.Commands());
        Assert.DoesNotContain("Validated", result.StandardOutput);
    }

    [Fact]
    public async Task FailedUpgrade_IsNotReportedAsSuccess()
    {
        PrepareApt();
        _fixture.Environment["RETROMIND_TEST_UPGRADE_EXIT"] = "43";

        var result = await UpgradeAsync();

        Assert.Equal(43, result.ExitCode);
        Assert.DoesNotContain("Validated", result.StandardOutput);
    }

    [Fact]
    public async Task FailedContainerValidation_PreservesLockAndRemovesCandidate()
    {
        var (script, lockPath) = PrepareUpdate();
        _fixture.Environment["RETROMIND_TEST_CONTAINER_EXIT"] = "44";

        var result = await _fixture.RunAsync(script);

        Assert.Equal(44, result.ExitCode);
        Assert.Equal("original reviewed lock\n", File.ReadAllText(lockPath));
        AssertNoUpdateCandidates(lockPath);
    }

    [Fact]
    public async Task SuccessfulUpdate_PublishesCompleteVerifiedCandidate()
    {
        var (script, lockPath) = PrepareUpdate();

        var result = await _fixture.RunAsync(script);

        result.AssertSuccess();
        var contents = File.ReadAllText(lockPath);
        Assert.Contains("DEBIAN_SNAPSHOT_SECURITY=20261105T000000Z", contents);
        Assert.Contains("DOTNET_SDK_VERSION=10.0.999", contents);
        Assert.Contains("DOTNET_SDK_LINUX_X64_SHA512=" + SdkChecksum(), contents);
        AssertNoUpdateCandidates(lockPath);
        var command = File.ReadAllText(_fixture.LogPath);
        Assert.Contains("retromind-bootstrap-ca.crt:ro", command);
        Assert.Contains("/usr/local/bin/upgrade-bookworm-packages", command);
    }

    [Fact]
    public async Task InvalidSdkChecksum_PreservesLockBeforeContainerExecution()
    {
        var (script, lockPath) = PrepareUpdate();
        File.WriteAllText(_fixture.Environment["RETROMIND_TEST_ARCHIVE"]!, "tampered test artifact");

        var result = await _fixture.RunAsync(script);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("original reviewed lock\n", File.ReadAllText(lockPath));
        Assert.False(File.Exists(_fixture.LogPath));
        AssertNoUpdateCandidates(lockPath);
    }

    [Fact]
    public async Task EmptyRecentSnapshotQuery_FallsBackToOriginalWindow()
    {
        var (script, lockPath) = PrepareUpdate();
        _fixture.Environment["RETROMIND_TEST_EMPTY_RECENT"] = "1";

        var result = await _fixture.RunAsync(script);

        result.AssertSuccess();
        Assert.True(File.Exists(_fixture.Environment["RETROMIND_TEST_FIRST_REQUEST"]));
        Assert.Contains("DEBIAN_SNAPSHOT_SECURITY=20261105T000000Z", File.ReadAllText(lockPath));
    }

    private void PrepareApt(string apt = "2.6.2", string library = "2.6.2")
    {
        _fixture.Environment["RETROMIND_TEST_APT"] = apt;
        _fixture.Environment["RETROMIND_TEST_LIBRARY"] = library;
        _fixture.Executable("apt-get", """
            printf '%s|%s\n' "${DEBIAN_FRONTEND:-}" "$*" >> "$RETROMIND_TEST_LOG"
            case "$*" in
              *' update') exit "${RETROMIND_TEST_UPDATE_EXIT:-0}" ;;
              *' upgrade -y') exit "${RETROMIND_TEST_UPGRADE_EXIT:-0}" ;;
              *) exit 99 ;;
            esac
            """);
        _fixture.Executable("dpkg-query", """
            case "$*" in
              *libapt-pkg6.0) printf '%s' "$RETROMIND_TEST_LIBRARY" ;;
              *) printf '%s' "$RETROMIND_TEST_APT" ;;
            esac
            """);
        // The host need not be Debian; real dpkg comparisons also run during container validation.
        _fixture.Executable("dpkg", """
            case "$*" in
              '--compare-versions 2.6.2 ge 2.6.2'|'--compare-versions 2.6.2+deb12u1 ge 2.6.2'|'--compare-versions 2.6.3 ge 2.6.2') exit 0 ;;
              '--compare-versions 2.6.1 ge 2.6.2'|'--compare-versions 2.6.2~rc1 ge 2.6.2') exit 1 ;;
              *) exit 99 ;;
            esac
            """);
    }

    private Task<AppImageScriptFixture.ScriptResult> UpgradeAsync() =>
        _fixture.RunAsync(AppImageScriptFixture.ScriptPath("upgrade-bookworm-packages.sh"));

    private static string SdkChecksum() =>
        Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes("verified test SDK artifact")));

    private (string Script, string LockPath) PrepareUpdate()
    {
        var build = _fixture.Files.CreateDirectory("build");
        _fixture.CopyBuildFiles(build, "update-appimage-environment.sh", "configure-bookworm-snapshot.sh", "upgrade-bookworm-packages.sh");
        var lockPath = _fixture.Files.CreateFile("build/appimage-environment.env", "original reviewed lock\n");
        var archive = _fixture.Files.CreateFile("sdk.tar.gz", "verified test SDK artifact");
        var metadata = _fixture.Files.CreateFile("releases.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["latest-sdk"] = "10.0.999",
            ["releases"] = new[]
            {
                new
                {
                    sdk = new
                    {
                        version = "10.0.999",
                        files = new[]
                        {
                            new { rid = "linux-x64", name = "dotnet-sdk-linux-x64.tar.gz",
                                url = "https://example.invalid/dotnet-sdk-10.0.999-linux-x64.tar.gz", hash = SdkChecksum() }
                        }
                    },
                    runtime = new { version = "10.0.99" }
                }
            }
        }));
        _fixture.Environment["SSL_CERT_FILE"] = _fixture.Files.CreateFile("ca.crt", "fake CA for the fake container");
        _fixture.Environment["RETROMIND_TEST_METADATA"] = metadata;
        _fixture.Environment["RETROMIND_TEST_ARCHIVE"] = archive;
        _fixture.Environment["RETROMIND_TEST_FIRST_REQUEST"] = _fixture.Files.GetPath("first-request");
        _fixture.Executable("curl", """
            destination= url=
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -o) destination="$2"; shift 2 ;;
                https://*) url="$1"; shift ;;
                *) shift ;;
              esac
            done
            case "$url" in
              *timestamp*)
                if [ "${RETROMIND_TEST_EMPTY_RECENT:-0}" = 1 ] && [ ! -e "$RETROMIND_TEST_FIRST_REQUEST" ]; then
                  touch "$RETROMIND_TEST_FIRST_REQUEST"
                  printf '%s\n' '{"result":{"debian":[],"debian-security":[]}}' > "$destination"
                else
                  printf '%s\n' '{"result":{"debian":["20261105T000000Z"],"debian-security":["20261105T000000Z"]}}' > "$destination"
                fi
                ;;
              *releases.json) cp "$RETROMIND_TEST_METADATA" "$destination" ;;
              *.tar.gz) cp "$RETROMIND_TEST_ARCHIVE" "$destination" ;;
              *) exit 97 ;;
            esac
            """);
        _fixture.Executable("docker", """
            case "$*" in
              'buildx imagetools inspect '*) printf 'sha256:%064d\n' 0 ;;
              'run '*)
                printf '%s\n' "$*" > "$RETROMIND_TEST_LOG"
                exit "${RETROMIND_TEST_CONTAINER_EXIT:-0}"
                ;;
              *) exit 98 ;;
            esac
            """);
        return (Path.Combine(build, "update-appimage-environment.sh"), lockPath);
    }

    private static void AssertNoUpdateCandidates(string lockPath) =>
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(lockPath)!, ".appimage-environment-update.*"));

    public void Dispose() => _fixture.Dispose();
}
