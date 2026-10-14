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
            command => Assert.Equal(["auth", "--code", "example-code"], command.Arguments),
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

        Assert.Contains("did not establish", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task CommandFailure_ReportsOperationAndSanitizedError()
    {
        var client = new LegendaryCliClient(
            new FakeProcessRunner(new LegendaryCliResult(7, string.Empty, "Login required")));

        var exception = await Assert.ThrowsAsync<LegendaryCliException>(() => client.GetStatusAsync());

        Assert.Contains("status check", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Login required", exception.Message, StringComparison.Ordinal);
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
}
