using System.Text;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class SteamProcessTrackingServiceTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData("On")]
    public void IsEnabledValue_AcceptsExplicitTruthyValues(string value)
    {
        Assert.True(SteamProcessTrackingService.IsEnabledValue(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("diagnostics")]
    public void IsEnabledValue_RejectsOtherValues(string? value)
    {
        Assert.False(SteamProcessTrackingService.IsEnabledValue(value));
    }

    [Fact]
    public void Prepare_RemainsAvailableWhenDiagnosticLoggingIsDisabled()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var messages = new List<string>();
        var service = new SteamProcessTrackingService(
            temp.RootPath,
            messages.Add,
            enabled: false,
            observationDuration: TimeSpan.FromSeconds(1),
            initialPollInterval: TimeSpan.FromMilliseconds(5),
            steadyPollInterval: TimeSpan.FromMilliseconds(5),
            quietPeriod: TimeSpan.FromMilliseconds(10),
            candidateConfirmationDuration: TimeSpan.FromMilliseconds(5));

        var session = service.Prepare(123, "Steam game");

        Assert.NotNull(session);
        Assert.Empty(messages);
    }

    [Fact]
    public void TryMatchSteamAppId_ReportsEveryMatchingSupportedVariable()
    {
        var environment = BuildEnvironment(
            "PATH=/usr/bin",
            "SteamAppId=457140",
            "SteamGameId=999",
            "STEAM_COMPAT_APP_ID=457140");

        var matched = SteamProcessTrackingService.TryMatchSteamAppId(
            environment,
            457140,
            out var matchedVariables);

        Assert.True(matched);
        Assert.Equal("SteamAppId,STEAM_COMPAT_APP_ID", matchedVariables);
    }

    [Fact]
    public void TryMatchSteamAppId_RequiresAnExactNumericValue()
    {
        var environment = BuildEnvironment(
            "SteamAppId=4571400",
            "MY_SteamGameId=457140",
            "STEAM_COMPAT_APP_ID=not-a-number");

        var matched = SteamProcessTrackingService.TryMatchSteamAppId(
            environment,
            457140,
            out var matchedVariables);

        Assert.False(matched);
        Assert.Empty(matchedVariables);
    }

    [Fact]
    public async Task ObserveAsync_LogsOnlyNewProcessesWithMatchingAppId()
    {
        using var temp = new TemporaryDirectory();
        CreateFakeProcess(temp, 111, 1, 457140, "already-running", "old-game");
        CreateFakeProcess(temp, 222, 42, 457140, "OxygenNotIncluded", "OxygenNotIncluded.exe");
        CreateFakeProcess(temp, 333, 42, 999, "different-game", "different-game");

        var messages = new List<string>();
        var service = new SteamProcessTrackingService(
            temp.RootPath,
            messages.Add,
            enabled: true,
            observationDuration: TimeSpan.FromMilliseconds(40),
            initialPollInterval: TimeSpan.FromMilliseconds(5),
            steadyPollInterval: TimeSpan.FromMilliseconds(5),
            quietPeriod: TimeSpan.FromMilliseconds(10),
            candidateConfirmationDuration: TimeSpan.FromMilliseconds(5));
        var session = new SteamProcessTrackingSession(
            1,
            457140,
            "Oxygen Not Included",
            new HashSet<int> { 111 });

        var observation = service.ObserveAsync(session, CancellationToken.None);
        await Task.Delay(15);
        Directory.Delete(temp.GetPath("222"), recursive: true);
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.WasTracked);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Contains(messages, message =>
            message.Contains("PID=222", StringComparison.Ordinal) &&
            message.Contains("PPID=42", StringComparison.Ordinal) &&
            message.Contains("Command=OxygenNotIncluded.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains("PID=111", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains("PID=333", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("PID=222", StringComparison.Ordinal) &&
            message.Contains("exited", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("session boundary observed", StringComparison.Ordinal) &&
            message.Contains("1 matching process instance(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ObserveAsync_StopsAfterDiscoveryTimeoutWhenNoProcessMatches()
    {
        using var temp = new TemporaryDirectory();
        var messages = new List<string>();
        var service = new SteamProcessTrackingService(
            temp.RootPath,
            messages.Add,
            enabled: true,
            observationDuration: TimeSpan.FromMilliseconds(20),
            initialPollInterval: TimeSpan.FromMilliseconds(5),
            steadyPollInterval: TimeSpan.FromMilliseconds(5),
            quietPeriod: TimeSpan.FromMilliseconds(10),
            candidateConfirmationDuration: TimeSpan.FromMilliseconds(5));
        var session = new SteamProcessTrackingSession(
            1,
            457140,
            "Missing game",
            new HashSet<int>());

        var result = await service.ObserveAsync(session, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.WasTracked);
        Assert.Contains(messages, message =>
            message.Contains("no matching process", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains("session boundary observed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("iscriptevaluator.exe", "iscriptevaluator.exe", "iscriptevaluator.exe", false)]
    [InlineData("VC_redist.x64.exe", "VC_redist.x64.exe", "VC_redist.x64.exe", false)]
    [InlineData("wineboot.exe", "wine64-preloader", "wineboot.exe", false)]
    [InlineData("crashpad_handler.exe", "wine64-preloader", "crashpad_handler.exe", false)]
    [InlineData("reaper", "reaper", "DRG Survivor.exe", false)]
    [InlineData("pressure-vessel", "pressure-vessel-wrap", "DRG Survivor.exe", false)]
    [InlineData("pv-adverb", "pv-adverb", "DRG Survivor.exe", false)]
    [InlineData("srt-bwrap", "srt-bwrap", "Achilles.exe", false)]
    [InlineData("bootstrapper-v2", "bootstrapper-v2", "bootstrapper-v2", false)]
    [InlineData("ClientLauncherG", "wine-preloader", "ClientLauncherG.exe", false)]
    [InlineData("Paradox Launche", "dash", "sh", false)]
    [InlineData("cpatch", "cpatch", "cpatch", false)]
    [InlineData("exe", "Launcher", "exe", false)]
    [InlineData("ldconfig", "ldconfig", "ldconfig", false)]
    [InlineData("python3", "python3.13", "DRG Survivor.exe", false)]
    [InlineData("rundll32.exe", "wine64-preloader", "rundll32.exe", false)]
    [InlineData("infinst.exe", "wine64-preloader", "infinst.exe", false)]
    [InlineData("msiexec", "wine-preloader", "msiexec", false)]
    [InlineData("UE4PrereqSetup_", "wine-preloader", "UE4PrereqSetup_x64.exe", false)]
    [InlineData("regsvr32.exe", "wine64-preloader", "regsvr32.exe", false)]
    [InlineData("UnityCrashHandl", "wine64-preloader", "UnityCrashHandler64.exe", false)]
    [InlineData("FactoryGame", "wine64-preloader", "FactoryGameSteam-Win64-Shipping.exe", true)]
    [InlineData("Unity Main Thre", "wine64-preloader", "DRG Survivor.exe", true)]
    [InlineData("sh", "dash", "sh", true)]
    [InlineData("Cities.x64", "Cities.x64", "Cities.x64", true)]
    [InlineData("OxygenNotInclud", "OxygenNotIncluded", "OxygenNotIncluded", true)]
    public void IsLikelyGameProcess_DistinguishesSetupHelpersFromGameCandidates(
        string processName,
        string executableName,
        string commandName,
        bool expected)
    {
        var match = new SteamProcessMatch(
            new SteamProcessIdentity(123, 456),
            123,
            1,
            processName,
            executableName,
            commandName,
            "SteamAppId");

        Assert.Equal(expected, SteamProcessTrackingService.IsLikelyGameProcess(match));
    }

    [Fact]
    public async Task ObserveAsync_ContinuesAfterShortSetupGroupUntilGameCandidateAppears()
    {
        using var temp = new TemporaryDirectory();
        var messages = new List<string>();
        var service = new SteamProcessTrackingService(
            temp.RootPath,
            messages.Add,
            enabled: true,
            observationDuration: TimeSpan.FromMilliseconds(200),
            initialPollInterval: TimeSpan.FromMilliseconds(5),
            steadyPollInterval: TimeSpan.FromMilliseconds(5),
            quietPeriod: TimeSpan.FromMilliseconds(10),
            candidateConfirmationDuration: TimeSpan.FromMilliseconds(10));
        var session = new SteamProcessTrackingSession(
            7,
            526870,
            "Satisfactory",
            new HashSet<int>());

        CreateFakeProcess(temp, 111, 1, 526870, "VC_redist.x64.e", "VC_redist.x64.exe");
        var observation = service.ObserveAsync(session, CancellationToken.None);
        await Task.Delay(20);
        Directory.Delete(temp.GetPath("111"), recursive: true);
        await Task.Delay(20);
        CreateFakeProcess(
            temp,
            222,
            1,
            526870,
            "FactoryGame",
            "FactoryGameSteam-Win64-Shipping.exe");
        await Task.Delay(30);
        Directory.Delete(temp.GetPath("222"), recursive: true);
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.WasTracked);
        Assert.Contains(messages, message =>
            message.Contains("helper/setup process group ended", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("likely game process confirmed", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("session boundary observed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Paradox Launche", "sh")]
    [InlineData("ClientLauncherG", "ClientLauncherG.exe")]
    public async Task ObserveAsync_StopsAfterRecognizedLauncherClosesWithoutStartingGame(
        string processName,
        string commandName)
    {
        using var temp = new TemporaryDirectory();
        var messages = new List<string>();
        var service = new SteamProcessTrackingService(
            temp.RootPath,
            messages.Add,
            enabled: true,
            observationDuration: TimeSpan.FromMilliseconds(200),
            initialPollInterval: TimeSpan.FromMilliseconds(5),
            steadyPollInterval: TimeSpan.FromMilliseconds(5),
            quietPeriod: TimeSpan.FromMilliseconds(10),
            candidateConfirmationDuration: TimeSpan.FromMilliseconds(5));
        var session = new SteamProcessTrackingSession(
            8,
            255710,
            "Cities: Skylines",
            new HashSet<int>());

        CreateFakeProcess(temp, 111, 1, 255710, processName, commandName);
        var observation = service.ObserveAsync(session, CancellationToken.None);
        await Task.Delay(20);
        Directory.Delete(temp.GetPath("111"), recursive: true);
        var result = await observation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.WasTracked);
        Assert.Contains(messages, message =>
            message.Contains("launcher closed", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains("startup observation finished", StringComparison.Ordinal));
    }

    private static byte[] BuildEnvironment(params string[] entries)
        => Encoding.ASCII.GetBytes(string.Join('\0', entries) + '\0');

    private static void CreateFakeProcess(
        TemporaryDirectory temp,
        int processId,
        int parentProcessId,
        uint appId,
        string processName,
        string commandName)
    {
        var processDirectory = temp.CreateDirectory(processId.ToString());
        File.WriteAllBytes(
            Path.Combine(processDirectory, "environ"),
            BuildEnvironment($"SteamAppId={appId}"));
        File.WriteAllText(Path.Combine(processDirectory, "comm"), processName);
        File.WriteAllText(
            Path.Combine(processDirectory, "stat"),
            $"{processId} ({processName}) S {parentProcessId} " +
            string.Join(' ', Enumerable.Repeat("0", 17)) +
            $" {(ulong)processId * 1000UL}");
        File.WriteAllBytes(
            Path.Combine(processDirectory, "cmdline"),
            Encoding.UTF8.GetBytes($"/games/{commandName}\0"));
    }
}
