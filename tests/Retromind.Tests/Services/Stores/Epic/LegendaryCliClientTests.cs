using System.Text.Json;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class LegendaryCliClientTests
{
    [Fact]
    public async Task GetVersionAsync_ParsesOfficialVersionFormat()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, "legendary version \"0.20.34\", codename \"Direct Intervention\"\n", string.Empty));
        var client = new LegendaryCliClient(runner);

        var version = await client.GetVersionAsync();

        Assert.Equal("0.20.34", version.Version);
        Assert.Equal("Direct Intervention", version.CodeName);
        Assert.Equal(["--version"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesAuthenticatedStatus()
    {
        const string json = """
            {
              "account": "Example Player",
              "games_available": 42,
              "games_installed": 3,
              "egl_sync_enabled": false,
              "config_directory": "/portable/Tools/Legendary/Data"
            }
            """;
        var runner = new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty));
        var client = new LegendaryCliClient(runner);

        var status = await client.GetStatusAsync();

        Assert.True(status.IsAuthenticated);
        Assert.Equal("Example Player", status.AccountName);
        Assert.Equal(42, status.AvailableGameCount);
        Assert.Equal(3, status.InstalledGameCount);
        Assert.False(status.EpicLauncherSyncEnabled);
        Assert.Equal("/portable/Tools/Legendary/Data", status.ConfigDirectory);
        Assert.Equal(["status", "--offline", "--json"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task GetStatusAsync_HidesLegendarysLoggedOutMarker()
    {
        const string json = """
            {
              "account": "<not logged in>",
              "games_available": 0,
              "games_installed": 0,
              "egl_sync_enabled": false,
              "config_directory": "/tmp/legendary"
            }
            """;
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty)));

        var status = await client.GetStatusAsync();

        Assert.False(status.IsAuthenticated);
        Assert.Null(status.AccountName);
    }

    [Fact]
    public async Task GetStatusAsync_RejectsInvalidJson()
    {
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(0, "not-json", string.Empty)));

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(() => client.GetStatusAsync());

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetValidatedStatusAsync_UsesOnlineStatusCheck()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, CreateAuthenticatedStatusJson(), string.Empty));
        var client = new LegendaryCliClient(runner);

        var status = await client.GetValidatedStatusAsync();

        Assert.True(status.IsAuthenticated);
        Assert.Equal(
            ["status", "--json"],
            Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task AuthenticateWithAuthorizationCodeAsync_VerifiesSessionOnline()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, string.Empty, "Login successful"),
            new LegendaryCliResult(0, CreateAuthenticatedStatusJson(), string.Empty));
        var client = new LegendaryCliClient(runner);

        var status = await client.AuthenticateWithAuthorizationCodeAsync("  example-code  ");

        Assert.True(status.IsAuthenticated);
        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal(["auth", "--disable-webview"], command.Arguments);
                Assert.Equal("example-code", command.StandardInput);
                Assert.True(command.SuppressBrowserLaunch);
                Assert.False(command.IncludeErrorDetails);
            },
            command => Assert.Equal(["status", "--json"], command.Arguments));
    }

    [Fact]
    public async Task AuthenticateWithAuthorizationCodeAsync_RejectsLogicalLoginFailure()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, string.Empty, "Login attempt failed"),
            new LegendaryCliResult(0, CreateLoggedOutStatusJson(), string.Empty));
        var client = new LegendaryCliClient(runner);

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(
            () => client.AuthenticateWithAuthorizationCodeAsync("invalid-code"));

        Assert.Contains("invalid or expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Login attempt failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateWithAuthorizationCodeAsync_HidesLegendaryErrorOutput()
    {
        const string secret = "sensitive-exchange-code";
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(
                1,
                string.Empty,
                $"Login failed: authorizationCode={secret}"));
        var client = new LegendaryCliClient(runner);

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(
            () => client.AuthenticateWithAuthorizationCodeAsync("one-time-code"));

        Assert.Contains("Epic sign-in failed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutAsync_DeletesLegendaryCredentials()
    {
        var runner = new FakeProcessRunner(new LegendaryCliResult(0, string.Empty, string.Empty));
        var client = new LegendaryCliClient(runner);

        await client.SignOutAsync();

        Assert.Equal(["auth", "--delete"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task GetGamesAsync_ParsesBaseGamesAndPrefersWindowsAsset()
    {
        const string json = """
            [
              {
                "app_name": "ExampleGame",
                "app_title": "Example Game",
                "asset_infos": {
                  "Mac": { "build_version": "mac-1" },
                  "Windows": { "build_version": "win-2" }
                },
                "dlcs": [
                  { "app_name": "IgnoredDlc", "app_title": "Ignored DLC" }
                ]
              },
              {
                "app_name": "AnotherGame",
                "app_title": "Another Game",
                "asset_infos": {
                  "Windows": { "build_version": "win-1" }
                },
                "dlcs": []
              }
            ]
            """;
        var runner = new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty));
        var client = new LegendaryCliClient(runner);

        var games = await client.GetGamesAsync();

        Assert.Collection(
            games,
            game =>
            {
                Assert.Equal("AnotherGame", game.AppName);
                Assert.Equal("Another Game", game.AppTitle);
                Assert.Equal("Windows", game.Platform);
                Assert.Equal("win-1", game.Version);
            },
            game =>
            {
                Assert.Equal("ExampleGame", game.AppName);
                Assert.Equal("Example Game", game.AppTitle);
                Assert.Equal("Windows", game.Platform);
                Assert.Equal("win-2", game.Version);
            });
        Assert.Equal(["list", "--json"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task GetGamesAsync_RejectsNonArrayResponse()
    {
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(0, "{}", string.Empty)));

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(() => client.GetGamesAsync());

        Assert.Contains("library load", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDlcCatalogAsync_CombinesOwnedAndInstalledLegendaryData()
    {
        const string catalogJson = """
            [
              {
                "app_name": "ExampleGame",
                "app_title": "Example Game",
                "asset_infos": {
                  "Windows": { "build_version": "base-2" }
                },
                "dlcs": [
                  {
                    "app_name": "DlcInstalled",
                    "app_title": "Installed DLC",
                    "asset_infos": {
                      "Windows": { "build_version": "dlc-2" }
                    }
                  },
                  {
                    "app_name": "DlcAvailable",
                    "app_title": "Available DLC",
                    "asset_infos": {
                      "Windows": { "build_version": "dlc-1" }
                    }
                  }
                ]
              }
            ]
            """;
        const string installedJson = """
            [
              {
                "app_name": "ExampleGame",
                "title": "Example Game",
                "install_path": "/games/Example",
                "version": "base-2",
                "platform": "Windows",
                "install_size": 4096,
                "is_dlc": false
              },
              {
                "app_name": "DlcInstalled",
                "title": "Installed DLC",
                "install_path": "/games/Example",
                "version": "dlc-1",
                "platform": "Windows",
                "install_size": 2048,
                "is_dlc": true
              }
            ]
            """;
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, catalogJson, string.Empty),
            new LegendaryCliResult(0, installedJson, string.Empty));
        var client = new LegendaryCliClient(runner);

        var dlcs = await client.GetDlcCatalogAsync("ExampleGame");

        Assert.Collection(
            dlcs,
            dlc =>
            {
                Assert.Equal("DlcAvailable", dlc.AppName);
                Assert.Equal("Available DLC", dlc.Title);
                Assert.Equal("dlc-1", dlc.AvailableVersion);
                Assert.False(dlc.IsInstalled);
                Assert.False(dlc.HasUpdateAvailable);
            },
            dlc =>
            {
                Assert.Equal("DlcInstalled", dlc.AppName);
                Assert.Equal("Installed DLC", dlc.Title);
                Assert.Equal("dlc-2", dlc.AvailableVersion);
                Assert.Equal("dlc-1", dlc.InstalledVersion);
                Assert.Equal(2048, dlc.InstalledSize);
                Assert.True(dlc.IsInstalled);
                Assert.True(dlc.HasUpdateAvailable);
            });
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["list", "--json"], command.Arguments),
            command => Assert.Equal(["list-installed", "--json"], command.Arguments));
    }

    [Fact]
    public async Task GetDlcCatalogAsync_ReturnsEmptyCatalogWhenGameHasNoDlcs()
    {
        const string catalogJson = """
            [
              {
                "app_name": "ExampleGame",
                "app_title": "Example Game",
                "asset_infos": {},
                "dlcs": []
              }
            ]
            """;
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, catalogJson, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty));
        var client = new LegendaryCliClient(runner);

        var dlcs = await client.GetDlcCatalogAsync("ExampleGame");

        Assert.Empty(dlcs);
    }

    [Fact]
    public async Task GetInstallEstimateAsync_SumsManifestFileSizes()
    {
        const string json = """
            [
              { "filename": "first.bin", "file_size": 1024 },
              { "filename": "second.bin", "file_size": 2048 }
            ]
            """;
        var runner = new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty));
        var client = new LegendaryCliClient(runner);

        var estimate = await client.GetInstallEstimateAsync("ExampleGame");

        Assert.Equal(3072, estimate.InstalledBytes);
        Assert.Equal(2, estimate.FileCount);
        Assert.Equal(
            ["list-files", "ExampleGame", "--force-download", "--platform", "Windows", "--json"],
            Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task GetInstalledGamesAsync_ParsesLegendaryInstallationData()
    {
        const string json = """
            [
              {
                "app_name": "ExampleGame",
                "title": "Example Game",
                "install_path": "/games/Example Game",
                "executable": "Binaries/Example.exe",
                "version": "1.2.3",
                "platform": "Windows",
                "install_size": 4096,
                "is_dlc": false
              }
            ]
            """;
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty)));

        var game = Assert.Single(await client.GetInstalledGamesAsync());

        Assert.Equal("ExampleGame", game.AppName);
        Assert.Equal("Example Game", game.Title);
        Assert.Equal("/games/Example Game", game.InstallPath);
        Assert.Equal("Binaries/Example.exe", game.Executable);
        Assert.Equal("1.2.3", game.Version);
        Assert.Equal(4096, game.InstallSize);
        Assert.False(game.IsDlc);
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.1.0", false)]
    public async Task GetUpdateStatusAsync_ComparesInstalledAndRefreshedAvailableVersion(
        string availableVersion,
        bool expectedUpdate)
    {
        const string installedJson = """
            [
              {
                "app_name": "ExampleGame",
                "title": "Example Game",
                "install_path": "/games/Example",
                "executable": "Example.exe",
                "version": "1.1.0",
                "platform": "Windows",
                "install_size": 4096,
                "is_dlc": false
              }
            ]
            """;
        var availableJson = $$"""
            [
              {
                "app_name": "ExampleGame",
                "app_title": "Example Game",
                "asset_infos": {
                  "Windows": { "build_version": {{JsonSerializer.Serialize(availableVersion)}} }
                }
              }
            ]
            """;
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, installedJson, string.Empty),
            new LegendaryCliResult(0, availableJson, string.Empty));
        var client = new LegendaryCliClient(runner);

        var status = await client.GetUpdateStatusAsync("ExampleGame");

        Assert.Equal("1.1.0", status.InstalledVersion);
        Assert.Equal(availableVersion, status.AvailableVersion);
        Assert.Equal(expectedUpdate, status.IsUpdateAvailable);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(
                ["list-installed", "--check-updates", "--json"],
                command.Arguments),
            command => Assert.Equal(["list", "--json"], command.Arguments));
    }

    [Fact]
    public async Task InstallAsync_UsesRequestedDirectoryAndVerifiesRegistration()
    {
        using var temp = new TempDirectory();
        var installPath = Path.Combine(temp.RootPath, "Example Game");
        var installedJson = $$"""
            [
              {
                "app_name": "ExampleGame",
                "title": "Example Game",
                "install_path": {{JsonSerializer.Serialize(installPath)}},
                "executable": "Example.exe",
                "version": "1.2.3",
                "platform": "Windows",
                "install_size": 4096,
                "is_dlc": false
              }
            ]
            """;
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, "[]", string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, installedJson, string.Empty));
        var client = new LegendaryCliClient(runner);

        var installed = await client.InstallAsync("ExampleGame", installPath);

        Assert.Equal("ExampleGame", installed.AppName);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["list-installed", "--json"], command.Arguments),
            command =>
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, command.Timeout);
                Assert.Equal(
                    [
                        "--yes", "install", "ExampleGame",
                        "--base-path", temp.RootPath,
                        "--game-folder", "Example Game",
                        "--platform", "Windows",
                        "--skip-sdl", "--skip-dlcs"
                    ],
                    command.Arguments);
            },
            command => Assert.Equal(["list-installed", "--json"], command.Arguments));
    }

    [Fact]
    public async Task InstallAsync_AcceptsEquivalentPathWithTrailingDirectorySeparator()
    {
        using var temp = new TempDirectory();
        var installPath = Path.Combine(temp.RootPath, "Example Game");
        var requestedPath = installPath + Path.DirectorySeparatorChar;
        var installedJson = $$"""
            [
              {
                "app_name": "ExampleGame",
                "title": "Example Game",
                "install_path": {{JsonSerializer.Serialize(installPath)}},
                "executable": "Example.exe",
                "version": "1.2.3",
                "platform": "Windows",
                "install_size": 4096,
                "is_dlc": false
              }
            ]
            """;
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, "[]", string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, installedJson, string.Empty));
        var client = new LegendaryCliClient(runner);

        var installed = await client.InstallAsync("ExampleGame", requestedPath);

        Assert.Equal(installPath, installed.InstallPath);
        Assert.Equal(
            [
                "--yes", "install", "ExampleGame",
                "--base-path", temp.RootPath,
                "--game-folder", "Example Game",
                "--platform", "Windows",
                "--skip-sdl", "--skip-dlcs"
            ],
            runner.Commands[1].Arguments);
    }

    [Fact]
    public async Task InstallAsync_UsesLegendaryRepairWithoutImplicitUpdate()
    {
        using var temp = new TempDirectory();
        var installPath = Path.Combine(temp.RootPath, "Example Game");
        var installedJson = CreateInstalledGameJson(installPath);
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, installedJson, string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, installedJson, string.Empty));
        var client = new LegendaryCliClient(runner);

        await client.InstallAsync("ExampleGame", installPath, repair: true);

        var repairCommand = runner.Commands[1];
        Assert.Equal("game repair", repairCommand.Operation);
        Assert.Contains("--repair", repairCommand.Arguments);
        Assert.DoesNotContain("--repair-and-update", repairCommand.Arguments);
    }

    [Fact]
    public async Task InstallAsync_RemovesMissingRegistrationBeforeInstallingAtNewPath()
    {
        using var temp = new TempDirectory();
        var missingOldPath = Path.Combine(temp.RootPath, "Old", "Example Game");
        var requestedPath = Path.Combine(temp.RootPath, "New", "Example Game");
        var oldRegistration = CreateInstalledGameJson(missingOldPath);
        var newRegistration = CreateInstalledGameJson(requestedPath);
        var log = new List<string>();
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, oldRegistration, string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, newRegistration, string.Empty));
        var client = new LegendaryCliClient(runner);

        var installed = await client.InstallAsync("ExampleGame", requestedPath, log.Add);

        Assert.Equal(requestedPath, installed.InstallPath);
        Assert.Contains(log, line => line.Contains("stale Legendary registration", StringComparison.Ordinal));
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["list-installed", "--json"], command.Arguments),
            command => Assert.Equal(
                [
                    "--yes", "uninstall", "ExampleGame",
                    "--skip-uninstaller", "--keep-files"
                ],
                command.Arguments),
            command => Assert.Equal(["list-installed", "--json"], command.Arguments),
            command => Assert.Equal(
                [
                    "--yes", "install", "ExampleGame",
                    "--base-path", Path.Combine(temp.RootPath, "New"),
                    "--game-folder", "Example Game",
                    "--platform", "Windows",
                    "--skip-sdl", "--skip-dlcs"
                ],
                command.Arguments),
            command => Assert.Equal(["list-installed", "--json"], command.Arguments));
    }

    [Fact]
    public async Task UninstallAsync_RemovesLegendaryRegistrationAndVerifiesResult()
    {
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty));
        var client = new LegendaryCliClient(runner);

        await client.UninstallAsync("ExampleGame", keepFiles: false);

        Assert.Collection(
            runner.Commands,
            command =>
            {
                Assert.Equal(
                    ["--yes", "uninstall", "ExampleGame", "--skip-uninstaller"],
                    command.Arguments);
                Assert.Equal(Timeout.InfiniteTimeSpan, command.Timeout);
            },
            command => Assert.Equal(["list-installed", "--json"], command.Arguments));
    }

    [Fact]
    public async Task InstallAsync_DoesNotReplaceRegistrationWhoseDirectoryStillExists()
    {
        using var temp = new TempDirectory();
        var registeredPath = Path.Combine(temp.RootPath, "Existing", "Example Game");
        var requestedPath = Path.Combine(temp.RootPath, "New", "Example Game");
        Directory.CreateDirectory(registeredPath);
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, CreateInstalledGameJson(registeredPath), string.Empty));
        var client = new LegendaryCliClient(runner);

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(
            () => client.InstallAsync("ExampleGame", requestedPath));

        Assert.Contains("already has this game registered", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["list-installed", "--json"], Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task InstallAsync_RejectsLegendarysLogicalSuccessWithoutRegistration()
    {
        using var temp = new TempDirectory();
        var runner = new FakeProcessRunner(
            new LegendaryCliResult(0, "[]", string.Empty),
            new LegendaryCliResult(0, string.Empty, string.Empty),
            new LegendaryCliResult(0, "[]", string.Empty));
        var client = new LegendaryCliClient(runner);

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(
            () => client.InstallAsync("ExampleGame", Path.Combine(temp.RootPath, "Example Game")));

        Assert.Contains("without registering", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateInstalledGameJson(string installPath) => $$"""
        [
          {
            "app_name": "ExampleGame",
            "title": "Example Game",
            "install_path": {{JsonSerializer.Serialize(installPath)}},
            "executable": "Example.exe",
            "version": "1.2.3",
            "platform": "Windows",
            "install_size": 4096,
            "is_dlc": false
          }
        ]
        """;

    [Fact]
    public async Task GetLaunchParametersAsync_PreservesLegendaryArgumentOrderAndEnvironment()
    {
        const string json = """
            {
              "game_parameters": ["-game", "value with spaces"],
              "user_parameters": ["-user"],
              "egl_parameters": ["-AUTH_PASSWORD=exchange-code", "-EpicPortal"],
              "environment": {
                "EpicApp": "ExampleGame",
                "LANG": "de_DE.UTF-8"
              }
            }
            """;
        var runner = new FakeProcessRunner(new LegendaryCliResult(0, json, string.Empty));
        var client = new LegendaryCliClient(runner);

        var parameters = await client.GetLaunchParametersAsync("ExampleGame");

        Assert.Equal(
            ["-game", "value with spaces", "-user", "-AUTH_PASSWORD=exchange-code", "-EpicPortal"],
            parameters.Arguments);
        Assert.Equal("ExampleGame", parameters.Environment["EpicApp"]);
        Assert.Equal("de_DE.UTF-8", parameters.Environment["LANG"]);
        Assert.Equal(
            ["launch", "ExampleGame", "--json", "--no-wine", "--skip-version-check"],
            Assert.Single(runner.Commands).Arguments);
    }

    [Fact]
    public async Task CommandFailure_ReportsOperationAndSanitizedError()
    {
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(7, string.Empty, "Login required")));

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(() => client.GetStatusAsync());

        Assert.Contains("status check", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Login required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandFailure_RedactsKnownCredentialFormats()
    {
        const string accessToken = "access-token-secret";
        const string bearerToken = "bearer-token-secret";
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(
                7,
                string.Empty,
                $"access_token={accessToken} Authorization: Bearer {bearerToken}")));

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(() => client.GetStatusAsync());

        Assert.DoesNotContain(accessToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bearerToken, exception.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.Message, StringComparison.Ordinal);
    }

    private static string CreateAuthenticatedStatusJson() => """
        {
          "account": "Example Player",
          "games_available": 42,
          "games_installed": 3,
          "egl_sync_enabled": false,
          "config_directory": "/portable/Tools/Legendary/Data"
        }
        """;

    private static string CreateLoggedOutStatusJson() => """
        {
          "account": "<not logged in>",
          "games_available": 0,
          "games_installed": 0,
          "egl_sync_enabled": false,
          "config_directory": "/portable/Tools/Legendary/Data"
        }
        """;

    private sealed class FakeProcessRunner(params LegendaryCliResult[] results) : ILegendaryCliProcessRunner
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

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "retromind-legendary-client-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        internal string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
