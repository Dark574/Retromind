using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class WinetricksServiceTests : IDisposable
{
    private readonly TemporaryDirectory _diagnosticRoot = new();
    private readonly EnvironmentVariableScope _dataRootEnvironment;

    public WinetricksServiceTests()
    {
        _dataRootEnvironment = new EnvironmentVariableScope(("APPIMAGE", _diagnosticRoot.GetPath("Retromind.AppImage")));
    }

    public void Dispose()
    {
        _dataRootEnvironment.Dispose();
        _diagnosticRoot.Dispose();
    }

    [Fact]
    public void PreparePrefix_CreatesWineDirectoriesAndPortableDriveMappings()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var service = new WinetricksService(libraryRoot, new RecordingErrorService());

        service.PreparePrefix(prefixRoot, isProton: false, isUmu: false);

        var dosDevices = Path.Combine(prefixRoot, "dosdevices");
        Assert.True(Directory.Exists(Path.Combine(prefixRoot, "drive_c")));
        Assert.Equal("../drive_c", new DirectoryInfo(Path.Combine(dosDevices, "c:")).LinkTarget);
        Assert.NotNull(new DirectoryInfo(Path.Combine(dosDevices, "r:")).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(libraryRoot, "Games")));
    }

    [Fact]
    public void PrepareExecution_UsesUmuAndPreservesQuotedVerbArguments()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var protonRoot = temp.CreateDirectory("GE-Proton");
        temp.CreateFile("GE-Proton/protonfixes/winetricks");
        var service = new WinetricksService(libraryRoot, new RecordingErrorService());

        var plan = service.PrepareExecution(new WinetricksRequest(
            prefixRoot,
            "vcrun2022 \"dxvk hud\"",
            new Dictionary<string, string> { ["PROTONPATH"] = protonRoot },
            IsProton: true,
            IsUmu: true,
            UmuRunnerPath: "/usr/bin/umu-run"));

        Assert.True(plan.UsesUmu);
        Assert.Equal("/usr/bin/umu-run", plan.FileName);
        Assert.Equal(["winetricks", "vcrun2022", "dxvk hud"], plan.Arguments);
        Assert.Equal(prefixRoot, plan.CompatRoot);
        Assert.Equal(prefixRoot, plan.WinePrefix);
        Assert.Equal(prefixRoot, plan.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(prefixRoot, plan.Environment["WINEPREFIX"]);
        Assert.Null(plan.MissingBundledWinetricksPath);
    }

    [Fact]
    public void PrepareExecution_FallsBackToSystemWinetricksWithProtonWineBinaries()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixRoot = temp.GetPath("Library", "Prefixes", "Game");
        var protonRoot = temp.CreateDirectory("GE-Proton");
        var wine = temp.CreateFile("GE-Proton/files/bin/wine");
        var wineserver = temp.CreateFile("GE-Proton/files/bin/wineserver");
        var wine64 = temp.CreateFile("GE-Proton/files/bin/wine64");
        var service = new WinetricksService(libraryRoot, new RecordingErrorService());

        var plan = service.PrepareExecution(new WinetricksRequest(
            prefixRoot,
            "corefonts",
            new Dictionary<string, string> { ["PROTONPATH"] = protonRoot },
            IsProton: true,
            IsUmu: true,
            UmuRunnerPath: "/usr/bin/umu-run"));

        Assert.False(plan.UsesUmu);
        Assert.Equal("winetricks", plan.FileName);
        Assert.Equal(["corefonts"], plan.Arguments);
        Assert.Equal(Path.Combine(prefixRoot, "pfx"), plan.WinePrefix);
        Assert.Equal(wine, plan.Environment["WINE"]);
        Assert.Equal(wineserver, plan.Environment["WINESERVER"]);
        Assert.Equal(wine64, plan.Environment["WINE64"]);
        Assert.StartsWith(Path.GetDirectoryName(wine), plan.Environment["PATH"], StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(protonRoot, "protonfixes", "winetricks"),
            plan.MissingBundledWinetricksPath);
    }

    [Fact]
    public void ResolvePrefixPaths_UsesExistingRootStyleProtonPrefix()
    {
        using var temp = new TemporaryDirectory();
        var prefixRoot = temp.CreateDirectory("prefix");
        temp.CreateFile("prefix/system.reg");

        var paths = WinetricksService.ResolvePrefixPaths(
            prefixRoot,
            isProton: true,
            isUmu: false);

        Assert.Equal(prefixRoot, paths.CompatRoot);
        Assert.Equal(prefixRoot, paths.WinePrefix);
    }

    [Fact]
    public async Task Run_PreparationFailureIsReportedWithoutStartingAProcess()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var starts = 0;
        var service = new WinetricksService(temp.CreateDirectory("Library"), errors,
            _ => { starts++; throw new InvalidOperationException("Unexpected start"); });
        var log = new List<string>();

        var result = await service.RunAsync(
            new WinetricksRequest(
                PrefixRoot: string.Empty,
                Verbs: "corefonts",
                EnvironmentOverrides: new Dictionary<string, string>(),
                IsProton: false,
                IsUmu: false,
                UmuRunnerPath: "umu-run"),
            log.Add);

        var message = Assert.Single(log);
        Assert.StartsWith("Error while preparing Winetricks:", message, StringComparison.Ordinal);
        Assert.False(result.Success);
        Assert.Equal(WinetricksFailureCategory.Preparation, result.FailureCategory);
        Assert.Null(result.ExitCode);
        Assert.Equal("Winetricks.PreparationFailed", Assert.Single(errors.Reports).Code);
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task Run_ReturnsRealExitCodeAndPreservesBothOutputStreams(int exitCode)
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, $"printf 'stdout marker\\n'\nprintf 'stderr marker\\n' >&2\nexit {exitCode}"));
        var log = new ConcurrentQueue<string>();

        var result = await service.RunAsync(Request(temp), log.Enqueue);

        Assert.Equal(exitCode == 0, result.Success);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(exitCode == 0 ? WinetricksFailureCategory.None : WinetricksFailureCategory.ProcessExecution,
            result.FailureCategory);
        Assert.Contains("stdout marker", log);
        Assert.Contains("stderr marker", log);
        Assert.Contains($"Exit code: {exitCode}", log);
        if (exitCode == 0)
            Assert.Empty(errors.Reports);
        else
        {
            var report = Assert.Single(errors.Reports);
            Assert.Equal("Winetricks.ProcessFailed", report.Code);
            Assert.Equal(ErrorSeverity.Warning, report.Severity);
            Assert.Equal(nameof(WinetricksService), report.Component);
            Assert.Equal("17", report.Context!["ExitCode"]);
            Assert.Equal("Wine", report.Context["RunnerType"]);
            Assert.Equal(Request(temp).PrefixRoot, report.Context["PrefixPath"]);
        }
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_MissingOrNonExecutableProgramReportsExactlyOneStartFailure(bool permissionDenied)
    {
        using var temp = new TemporaryDirectory();
        var program = permissionDenied ? temp.CreateFile("not-executable", "#!/bin/sh\nexit 0\n") : temp.GetPath("missing");
        if (permissionDenied)
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var starts = 0;
        var service = new WinetricksService(temp.GetPath("Library"), errors, info =>
        {
            starts++;
            info.FileName = program;
            return Process.Start(info);
        });

        var result = await service.RunAsync(Request(temp), _ => { });

        Assert.False(result.Success);
        Assert.Equal(WinetricksFailureCategory.ProcessStart, result.FailureCategory);
        Assert.Null(result.ExitCode);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.StartFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal("Start", report.Context!["Operation"]);
        Assert.Equal(1, starts);
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task Run_NullProcessResultIsStartFailureWithoutDuplicateReports()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors, _ => null);
        var result = await service.RunAsync(Request(temp), _ => { });
        Assert.Equal(WinetricksFailureCategory.ProcessStart, result.FailureCategory);
        Assert.Null(result.ExitCode);
        Assert.Equal("Winetricks.StartFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task Run_BlockedPrefixReturnsPreparationFailureAndDoesNotStartProcess()
    {
        using var temp = new TemporaryDirectory();
        var prefix = temp.CreateFile("blocked-prefix");
        var errors = new RecordingErrorService();
        var starts = 0;
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            _ => { starts++; return null; });
        var result = await service.RunAsync(Request(temp) with { PrefixRoot = prefix }, _ => { });
        Assert.False(result.Success);
        Assert.Equal(WinetricksFailureCategory.Preparation, result.FailureCategory);
        Assert.Equal(0, starts);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.PreparationFailed", report.Code);
        Assert.Equal("PrepareExecution", report.Context!["Operation"]);
    }

    [Fact]
    public void PreparePrefix_ReportsMandatoryDirectoryFailureAndPreservesExceptionPolicy()
    {
        using var temp = new TemporaryDirectory();
        var prefix = temp.CreateFile("blocked-prefix");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors);
        Assert.ThrowsAny<IOException>(() => service.PreparePrefix(prefix, false, false));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.PreparationFailed", report.Code);
        Assert.Equal("PreparePrefix", report.Context!["Operation"]);
    }

    [Theory]
    [InlineData("Library/Prefixes/Game/dosdevices")]
    [InlineData("Library/Games")]
    public async Task Run_OptionalMappingFailureIncludingNestedHelperDoesNotPreventExecution(string blockedPath)
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile(blockedPath);
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "exit 0"));

        var result = await service.RunAsync(Request(temp), _ => { });

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(temp.GetPath(blockedPath)));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.PrefixMappingFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
    }

    [Fact]
    public async Task Run_ExternalPrefixDoesNotReportMissingOptionalGamesDriveAsFailure()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "exit 0"));
        var result = await service.RunAsync(Request(temp) with { PrefixRoot = temp.GetPath("external-prefix") }, _ => { });
        Assert.True(result.Success);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Run_LogReadFailureIsDiagnosticAndDoesNotReplaceSuccessfulResult()
    {
        using var temp = new TemporaryDirectory();
        temp.CreateFile("Library/Prefixes/Game/winetricks.log", "corefonts");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "exit 0"), _ => throw new IOException("Read failed"));
        var result = await service.RunAsync(Request(temp), _ => { });
        Assert.True(result.Success);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.LogReadFailed", report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
    }

    [Fact]
    public async Task Run_MonitoringFailureIsDistinctFromStartFailureAndReapsStartedProcess()
    {
        using var temp = new TemporaryDirectory();
        var starter = WinetricksTestProgram.CreateStarter(temp, "exec /bin/sleep 60");
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors, info =>
        {
            info.RedirectStandardOutput = false; // Deterministically fail BeginOutputReadLine after a real start.
            return starter(info);
        });

        var result = await service.RunAsync(Request(temp), _ => { }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Success);
        Assert.Equal(WinetricksFailureCategory.ProcessExecution, result.FailureCategory);
        Assert.NotNull(result.ExitCode);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Winetricks.ProcessFailed", report.Code);
        Assert.Equal("MonitorProcess", report.Context!["Operation"]);
    }

    [Fact]
    public async Task Run_ThrowingLogObserverDoesNotEscapeOutputCallbacksOrChangeExitResult()
    {
        using var temp = new TemporaryDirectory();
        var service = new WinetricksService(temp.GetPath("Library"), new RecordingErrorService(),
            WinetricksTestProgram.CreateStarter(temp, "printf 'stdout\\n'\nprintf 'stderr\\n' >&2\nexit 0"));
        var result = await service.RunAsync(Request(temp), _ => throw new InvalidOperationException("Log window closed"));
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Run_CancellationTerminatesStartedProcessWithoutReportingExecutionFailure()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors,
            WinetricksTestProgram.CreateStarter(temp, "printf 'READY\\n'\nexec /bin/sleep 60"));
        var running = service.RunAsync(Request(temp), line => { if (line == "READY") ready.TrySetResult(); }, cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsCancelled);
            Assert.False(result.Success);
            Assert.NotNull(result.ExitCode);
            Assert.Empty(errors.Reports);
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Run_PreCancelledOperationDoesNotCreatePrefixOrStartProcess()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var starts = 0;
        var errors = new RecordingErrorService();
        var service = new WinetricksService(temp.GetPath("Library"), errors, _ => { starts++; return null; });
        var result = await service.RunAsync(Request(temp), _ => { }, cancellation.Token);
        Assert.True(result.IsCancelled);
        Assert.Equal(0, starts);
        Assert.False(Directory.Exists(Request(temp).PrefixRoot));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Run_RedactsCredentialsInArgumentsOutputResultAndCentralLogWithoutPersistingEnvironment()
    {
        using var temp = new TemporaryDirectory();
        const string hostSecret = "synthetic-host-secret";
        using var environment = new EnvironmentVariableScope(("RETROMIND_TEST_API_TOKEN", hostSecret));
        const string argumentSecret = "argument secret with spaces";
        const string overrideSecret = "synthetic-override-secret";
        var errorService = new ErrorService(temp.GetPath("Logs", "Exceptions"));
        var service = new WinetricksService(temp.GetPath("Library"), errorService,
            _ => throw new IOException($"Cannot start: {argumentSecret} {overrideSecret} {hostSecret}"));
        var log = new ConcurrentQueue<string>();
        var request = Request(temp) with
        {
            Verbs = $"corefonts --password '{argumentSecret}'",
            EnvironmentOverrides = new Dictionary<string, string> { ["API_TOKEN"] = overrideSecret }
        };

        var result = await service.RunAsync(request, log.Enqueue);
        var persisted = File.ReadAllText(temp.GetPath("Logs", "Exceptions", "errors.jsonl"));
        foreach (var secret in new[] { argumentSecret, overrideSecret, hostSecret })
        {
            Assert.DoesNotContain(secret, string.Join('\n', log));
            Assert.DoesNotContain(secret, result.ErrorMessage!);
            Assert.DoesNotContain(secret, persisted);
        }
        using var json = JsonDocument.Parse(persisted);
        var context = json.RootElement.GetProperty("Context");
        Assert.False(context.TryGetProperty("Arguments", out _));
        Assert.False(context.TryGetProperty("Environment", out _));
        Assert.Equal("Winetricks.StartFailed", json.RootElement.GetProperty("Code").GetString());
    }

    internal static WinetricksRequest Request(TemporaryDirectory temp) => new(
        temp.GetPath("Library", "Prefixes", "Game"), "corefonts", new Dictionary<string, string>(), false, false, "umu-run");

    [Theory]
    [InlineData("vcrun2022 corefonts", "vcrun2022", "corefonts")]
    [InlineData("'dot net' \"dxvk hud\"", "dot net", "dxvk hud")]
    public void SplitArguments_PreservesQuotedGroups(
        string input,
        string first,
        string second)
    {
        var arguments = WinetricksService.SplitArguments(input);

        Assert.Equal([first, second], arguments);
    }
}
