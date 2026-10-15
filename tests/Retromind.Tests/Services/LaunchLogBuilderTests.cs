using System.Diagnostics;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class LaunchLogBuilderTests
{
    [Fact]
    public void Build_RedactsSecretsFromGameUriWithoutRemovingSafeQueryParameters()
    {
        var item = new MediaItem("URI launch")
        {
            MediaType = MediaType.Command
        };
        var settings = new AppSettings();
        var builder = new LaunchLogBuilder(
            item,
            emulator: null,
            settings,
            wrappers: null,
            recordsStatistics: false);
        const string environmentSecret = "environment-secret";
        const string uriSecret = "uri-secret";
        var uri = $"heroic://launch/example?token={uriSecret}&payload={environmentSecret}&mode=test";
        var startInfo = new ProcessStartInfo
        {
            FileName = "xdg-open",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(uri);
        startInfo.Environment["TEST_TOKEN"] = environmentSecret;
        var environment = new Dictionary<string, string>
        {
            ["TEST_TOKEN"] = environmentSecret
        };

        builder.CaptureProcessStart(startInfo, uri, environment);

        var log = builder.Build("Started", TimeSpan.Zero);

        const string redactedUri =
            "heroic://launch/example?token=<redacted>&payload=<redacted>&mode=test";
        Assert.Contains($"Arguments: {redactedUri}", log);
        Assert.Contains($"Game: {redactedUri}", log);
        Assert.Contains("TEST_TOKEN=<redacted>", log);
        Assert.DoesNotContain(uriSecret, log);
        Assert.DoesNotContain(environmentSecret, log);
    }

    [Fact]
    public void Build_RedactsLegendaryExchangeCodeFromEpicArguments()
    {
        var item = new MediaItem("Epic launch") { MediaType = MediaType.Emulator };
        var builder = new LaunchLogBuilder(
            item,
            emulator: null,
            new AppSettings(),
            wrappers: null,
            recordsStatistics: true);
        var startInfo = new ProcessStartInfo
        {
            FileName = "umu-run",
            Arguments = "game.exe -AUTH_LOGIN=unused -AUTH_PASSWORD=secret-exchange-code -AUTH_TYPE=exchangecode"
        };

        builder.CaptureProcessStart(startInfo, "game.exe", effectiveEnvironment: null);
        var log = builder.Build("Started", TimeSpan.Zero);

        Assert.Contains("-AUTH_PASSWORD=<redacted>", log);
        Assert.DoesNotContain("secret-exchange-code", log);
    }
}
