using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Services.Stores.Gog.Playtime;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LauncherServiceTests
{
    [Theory]
    [InlineData("steam", "steam://rungameid/123", "-silent steam://rungameid/123")]
    [InlineData("/usr/bin/steam", "steam://rungameid/123", "-silent steam://rungameid/123")]
    [InlineData("steam", "-silent steam://rungameid/123", "-silent steam://rungameid/123")]
    [InlineData("steam", "-SILENT steam://rungameid/123", "-SILENT steam://rungameid/123")]
    [InlineData("heroic", "steam://rungameid/123", "steam://rungameid/123")]
    public void BuildLinuxCommandArguments_AddsSilentOnlyToSteamGameCommands(
        string target,
        string arguments,
        string expected)
    {
        var item = new MediaItem("Steam game")
        {
            LauncherArgs = arguments
        };

        Assert.Equal(expected, LauncherService.BuildLinuxCommandArguments(item, target));
    }

    [Fact]
    public void BuildLinuxCommandArguments_DoesNotChangeUnidentifiedSteamCommand()
    {
        var item = new MediaItem("Steam client")
        {
            LauncherArgs = "-console"
        };

        Assert.Equal("-console", LauncherService.BuildLinuxCommandArguments(item, "steam"));
    }

    [Fact]
    public void AppendLaunchArguments_QuotesDynamicArgumentsWithoutChangingExistingPlan()
    {
        var result = LauncherService.AppendLaunchArguments(
            "\"/games/Example Game.exe\" -windowed",
            ["-AUTH_PASSWORD=abc", "value with spaces", "embedded\"quote", string.Empty]);

        Assert.Equal(
            "\"/games/Example Game.exe\" -windowed -AUTH_PASSWORD=abc \"value with spaces\" \"embedded\\\"quote\" \"\"",
            result);
    }

    [Fact]
    public async Task LaunchAsync_RunsNestedWrapperWhosePathContainsSpaces()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var outerWrapperPath = temp.CreateFile(
            "outer-wrapper.sh",
            "#!/bin/sh\nexec \"$@\"\n");
        var innerWrapperPath = temp.CreateFile(
            Path.Combine("Inner  Wrapper", "inner-wrapper.sh"),
            "#!/bin/sh\nexec \"$@\"\n");
        var gamePath = temp.CreateFile(
            Path.Combine("Game  Files", "game.sh"),
            "#!/bin/sh\nprintf 'nested-wrapper-ok\\n'\nexit 23\n");
        foreach (var executablePath in new[] { outerWrapperPath, innerWrapperPath, gamePath })
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var item = new MediaItem("Nested wrappers")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = gamePath
                }
            ]
        };
        LaunchWrapper[] wrappers =
        [
            new() { Path = outerWrapperPath },
            new() { Path = innerWrapperPath }
        ];
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(
            item,
            nativeWrappers: wrappers,
            recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.Contains("nested-wrapper-ok", result.ConsoleOutput);
    }

    [Fact]
    public async Task LaunchAsync_NormalizesExplicitRelativePrefixBeforeSettingEnvironment()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var expectedPrefix = temp.GetPath("Library", "Prefixes", "Trailing Slash Game");
        var scriptPath = temp.CreateFile(
            "print-prefix.sh",
            "#!/bin/sh\nprintf 'WINEPREFIX=%s\\n' \"$WINEPREFIX\" >&2\nexit 23\n");
        var item = new MediaItem("Trailing Slash Game")
        {
            MediaType = MediaType.Native,
            PrefixPath = "Prefixes/Trailing Slash Game//",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var service = new LauncherService(
            libraryRoot,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.Contains($"WINEPREFIX={expectedPrefix}", result.ConsoleOutput);
        Assert.True(Directory.Exists(expectedPrefix));
    }

    [Fact]
    public async Task LaunchAsync_ReturnsFailureWhenExecutableDoesNotExist()
    {
        using var temp = new TemporaryDirectory();
        var missingExecutable = temp.GetPath("missing-game-executable");
        var item = new MediaItem("Missing Game")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = missingExecutable
                }
            ]
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.False(result.IsStarted);
        Assert.False(result.WasSessionTracked);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Contains(missingExecutable, result.ErrorMessage);
        Assert.Equal(0, item.PlayCount);
        Assert.Null(item.LastPlayed);
    }

    [Fact]
    public async Task LaunchAsync_ReturnsCapturedOutputWhenProcessExitsEarlyWithError()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile(
            "failing-launch.sh",
            "#!/bin/sh\nprintf 'helpful launcher error\\n' >&2\ni=0\nwhile [ \"$i\" -lt 300 ]; do\n  printf 'diagnostic-line-%03d-xxxxxxxxxxxxxxxx\\n' \"$i\"\n  i=$((i + 1))\ndone\nprintf 'launcher output\\n'\nexit 23\n");
        var item = new MediaItem("Failing Launcher")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.True(result.WasSessionTracked);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("helpful launcher error", result.ConsoleOutput);
        Assert.Contains("launcher output", result.ConsoleOutput);
        Assert.True(result.ConsoleOutput!.Length <= 4100);
    }

    [Fact]
    public async Task LaunchAsync_InvalidUtf8OutputDoesNotBreakLaunchTracking()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile(
            "invalid-utf8-output.sh",
            "#!/bin/sh\nprintf '\\200\\201\\202\\377stdout-marker\\n'\nprintf '\\377\\202\\201\\200stderr-marker\\n' >&2\nexit 23\n");
        var item = new MediaItem("Invalid UTF-8 output")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.True(result.WasSessionTracked);
        Assert.Equal(23, result.ExitCode);
        Assert.Contains("stdout-marker", result.ConsoleOutput);
        Assert.Contains("stderr-marker", result.ConsoleOutput);
    }

    [Fact]
    public async Task LaunchAsync_ReportsTrackedSessionWhenProcessExitWasObserved()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("successful-launch.sh", "#!/bin/sh\nexit 0\n");
        var item = new MediaItem("Successful Launch")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.True(result.WasSessionTracked);
    }

    [Fact]
    public async Task LaunchAsync_CancelledTrackingDoesNotRecordOrReportCompletedSession()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var sleepPath = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        using var temp = new TemporaryDirectory();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var item = new MediaItem("Cancelled tracking")
        {
            MediaType = MediaType.Command,
            LauncherArgs = "1",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = sleepPath
                }
            ]
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, cancellationToken: cancellationTokenSource.Token);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.False(result.WasSessionTracked);
        Assert.Equal(0, item.PlayCount);
        Assert.Equal(TimeSpan.Zero, item.TotalPlayTime);
        Assert.Null(item.LastPlayed);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotTreatNonSteamStoreCommandAsTrackedGameSession()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var heroicPath = temp.CreateFile("heroic", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            heroicPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new MediaItem("Heroic handoff")
        {
            MediaType = MediaType.Command,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = heroicPath
                }
            ],
            LauncherArgs = "heroic://launch/example"
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.False(result.WasSessionTracked);
    }

    [Theory]
    [InlineData(MediaType.Command)]
    [InlineData(MediaType.Native)]
    public async Task LaunchAsync_TracksSteamAppIdAndReportsCompletedSession(MediaType mediaType)
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var steamPath = temp.CreateFile("steam", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            steamPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new MediaItem("Tracked Steam game")
        {
            MediaType = mediaType,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = steamPath
                }
            ],
            LauncherArgs = "steam://rungameid/123"
        };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "steam";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
        var steamTracker = new StubSteamProcessTrackingService(TimeSpan.FromSeconds(42));
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")),
            steamProcessTrackingService: steamTracker);

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.True(result.WasSessionTracked);
        Assert.Equal(1, steamTracker.PrepareCalls);
        Assert.Equal(1, steamTracker.TrackCalls);
    }

    [Fact]
    public async Task LaunchAsync_CancelledSteamTrackingDoesNotRecordAPartialSession()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var item = CreateSteamCommandItem(temp, "Cancelled Steam game");
        var steamTracker = new BlockingSteamProcessTrackingService();
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")),
            steamProcessTrackingService: steamTracker);

        var result = await service.LaunchAsync(
            item,
            cancellationToken: cancellationTokenSource.Token);

        Assert.False(result.WasSessionTracked);
        Assert.Equal(0, item.PlayCount);
        Assert.Equal(TimeSpan.Zero, item.TotalPlayTime);
        Assert.Null(item.LastPlayed);
    }

    [Fact]
    public async Task LaunchAsync_ManualWatchProcessTakesPriorityOverSteamAppIdTracking()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var item = CreateSteamCommandItem(temp, "Manually tracked Steam game");
        item.OverrideWatchProcess = "retromind-process-that-does-not-exist";
        var steamTracker = new StubSteamProcessTrackingService(TimeSpan.FromSeconds(42));
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")),
            steamProcessTrackingService: steamTracker);

        await service.LaunchAsync(item, cancellationToken: cancellationTokenSource.Token);

        Assert.Equal(0, steamTracker.PrepareCalls);
        Assert.Equal(0, steamTracker.TrackCalls);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotMarkFailedSteamHandoffAsTrackedSession()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var steamPath = temp.CreateFile("steam", "#!/bin/sh\nexit 23\n");
        File.SetUnixFileMode(
            steamPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new MediaItem("Failed Steam handoff")
        {
            MediaType = MediaType.Command,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = steamPath
                }
            ],
            LauncherArgs = "steam://rungameid/123"
        };
        var service = new LauncherService(
            temp.RootPath,
            new AppSettings(),
            new LaunchLogService(temp.GetPath("launch-logs")));

        var result = await service.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.Equal(23, result.ExitCode);
        Assert.False(result.WasSessionTracked);
    }

    [Theory]
    [InlineData(MediaType.Command, "steam", "steam://rungameid/123")]
    [InlineData(MediaType.Native, "steam", "steam://rungameid/123")]
    [InlineData(MediaType.Command, "heroic", "epic://example")]
    [InlineData(MediaType.Command, "steam://rungameid/123", "")]
    [InlineData(MediaType.Command, "xdg-open", "heroic://example")]
    public void IsDelegatedCommand_RecognizesStoreAndProtocolHandoffs(
        MediaType mediaType,
        string target,
        string arguments)
    {
        var item = new MediaItem("Delegated command")
        {
            MediaType = mediaType,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = target
                }
            ],
            LauncherArgs = arguments
        };

        Assert.True(LauncherService.IsDelegatedCommand(item));
    }

    [Fact]
    public void IsDelegatedCommand_DoesNotClassifyRegularCommandAsHandoff()
    {
        var item = new MediaItem("Regular command")
        {
            MediaType = MediaType.Command,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "/usr/bin/example-game"
                }
            ]
        };

        Assert.False(LauncherService.IsDelegatedCommand(item));
    }

    [Fact]
    public async Task LaunchAsync_PersistsCopyableLogAndRedactsSecrets()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile(
            "logged-launch.sh",
            "#!/bin/sh\nprintf 'launch-log-marker\\n' >&2\nexit 23\n");
        var item = new MediaItem("Logged Game")
        {
            MediaType = MediaType.Native,
            LauncherArgs = "--token command-secret --mode test",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        item.EnvironmentOverrides["TEST_TOKEN"] = "environment-secret";
        item.EnvironmentOverrides["DXVK_HUD"] = "fps";
        var launchLogs = new LaunchLogService(temp.GetPath("launch-logs"));
        var service = new LauncherService(temp.RootPath, new AppSettings(), launchLogs);

        var result = await service.LaunchAsync(
            item,
            environmentOverrides: item.EnvironmentOverrides,
            recordStatistics: false);
        var log = await launchLogs.TryReadAsync(item.Id);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.NotNull(log);
        Assert.Contains("Title: Logged Game", log);
        Assert.Contains($"Executable: {scriptPath}", log);
        Assert.Contains("Arguments: --token <redacted> --mode test", log);
        Assert.Contains("TEST_TOKEN=<redacted>", log);
        Assert.Contains("DXVK_HUD=fps", log);
        Assert.Contains("Exit code: 23", log);
        Assert.Contains("launch-log-marker", log);
        Assert.DoesNotContain("command-secret", log);
        Assert.DoesNotContain("environment-secret", log);
    }

    [Fact]
    public async Task LaunchAsync_RedactsSecretsFromPreparedEpicLaunchThroughoutTheSavedLog()
    {
        if (!OperatingSystem.IsLinux())
            return;

        const string argumentSecret = "epic-exchange-secret";
        const string environmentSecret = "epic-environment-secret";
        using var temp = new TemporaryDirectory();
        var legendaryOutput = $$"""
            {
              "game_parameters": [],
              "user_parameters": [],
              "egl_parameters": [
                "-AUTH_LOGIN=unused",
                "-AUTH_PASSWORD={{argumentSecret}}",
                "-AUTH_TYPE=exchangecode"
              ],
              "environment": {
                "EPIC_AUTH_TOKEN": "{{environmentSecret}}"
              }
            }
            """;
        var epicLaunchService = new EpicLaunchService(
            new LegendaryCliClient(
                new SingleResultLegendaryProcessRunner(
                    new LegendaryCliResult(0, legendaryOutput, string.Empty))));
        var preparation = await epicLaunchService.PrepareAsync("ExampleGame");
        var scriptPath = temp.CreateFile(
            "epic-launch.sh",
            "#!/bin/sh\nprintf '%s\\n' \"$@\" >&2\nprintf 'env=%s\\n' \"$EPIC_AUTH_TOKEN\" >&2\nexit 23\n");
        var item = new MediaItem("Epic launch")
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = scriptPath
                }
            ]
        };
        var launchLogs = new LaunchLogService(temp.GetPath("launch-logs"));
        var launcher = new LauncherService(temp.RootPath, new AppSettings(), launchLogs);

        var result = await launcher.LaunchAsync(
            item,
            environmentOverrides: preparation.Environment,
            additionalLaunchArguments: preparation.Arguments,
            recordStatistics: false);
        var log = await launchLogs.TryReadAsync(item.Id);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.NotNull(log);
        Assert.Contains("-AUTH_PASSWORD=<redacted>", log);
        Assert.Contains("EPIC_AUTH_TOKEN=<redacted>", log);
        Assert.DoesNotContain(argumentSecret, log);
        Assert.DoesNotContain(environmentSecret, log);
    }

    [Fact]
    public async Task LaunchAsync_CompletesCometSessionForEnabledGogGame()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-game.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG game", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.True(result.IsStarted);
        Assert.Equal(1, cometService.StartCalls);
        Assert.Equal(1, cometService.Session.CompleteCalls);
        Assert.Equal(0, cometService.Session.DisposeCalls);
    }

    [Fact]
    public async Task LaunchAsync_ReturnsWhileCometCompletesInBackground()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-background-completion.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG background completion", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        var completionBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cometService.Session.CompletionBlocker = completionBlocker;
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        try
        {
            var result = await launcher
                .LaunchAsync(item, recordStatistics: false)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result.IsStarted);
            Assert.Equal(1, cometService.Session.CompleteCalls);
            Assert.False(completionBlocker.Task.IsCompleted);
        }
        finally
        {
            completionBlocker.TrySetResult();
            await launcher.StopPendingGogCometCompletionAsync();
        }
    }

    [Fact]
    public async Task PrepareForShutdownAsync_AllowsPendingCometToFinishWithinGracePeriod()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-shutdown-grace.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG shutdown grace", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        var completionBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cometService.Session.CompletionBlocker = completionBlocker;
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        await launcher.LaunchAsync(item, recordStatistics: false);
        var shutdownTask = launcher.PrepareForShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.False(shutdownTask.IsCompleted);
        completionBlocker.SetResult();
        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(cometService.Session.CompletionCancellationObserved);
    }

    [Fact]
    public async Task PrepareForShutdownAsync_CancelsPendingCometAfterGracePeriod()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-shutdown-timeout.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG shutdown timeout", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        cometService.Session.CompletionBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        await launcher.LaunchAsync(item, recordStatistics: false);
        await launcher
            .PrepareForShutdownAsync(TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cometService.Session.CompletionCancellationObserved);
    }

    [Fact]
    public async Task PrepareForShutdownAsync_WaitsForActiveGogLaunchLocalCompletion()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-shutdown-local.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG shutdown local completion", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService
        {
            StartBlocker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        var launchTask = launcher.LaunchAsync(item, recordStatistics: false);
        await cometService.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shutdownTask = launcher.PrepareForShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.False(shutdownTask.IsCompleted);
        cometService.StartBlocker.SetResult();
        await Task.WhenAll(launchTask, shutdownTask).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, cometService.Session.CompleteCalls);
        Assert.Equal(1, cometService.Session.DisposeCalls);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotStartCometWhenIntegrationIsDisabled()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-game.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG game", scriptPath);
        var cometService = new StubGogCometSessionService();
        var launcher = CreateLauncherWithComet(temp, new AppSettings(), cometService);

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.True(result.IsStarted);
        Assert.Equal(0, cometService.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_CometStartupFailureDoesNotBlockGogGame()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-game.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG game", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService
        {
            StartException = new CometProcessException("Comet test failure.")
        };
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.True(result.IsStarted);
        Assert.True(result.WasSessionTracked);
        Assert.Equal(1, cometService.StartCalls);
    }

    [Fact]
    public async Task LaunchAsync_DisposesCometImmediatelyWhenGameCannotStart()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateGogItem("Missing GOG game", temp.GetPath("missing-game"));
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.False(result.IsStarted);
        Assert.Equal(1, cometService.StartCalls);
        Assert.Equal(0, cometService.Session.CompleteCalls);
        Assert.Equal(1, cometService.Session.DisposeCalls);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotStartCometForLegacyExternalGogCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var heroicPath = temp.CreateFile("heroic", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            heroicPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("Delegated GOG game", heroicPath);
        item.MediaType = MediaType.Command;
        item.LauncherArgs = "heroic://launch/example";
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var cometService = new StubGogCometSessionService();
        var launcher = CreateLauncherWithComet(temp, settings, cometService);

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.True(result.IsStarted);
        Assert.Equal(0, cometService.StartCalls);
    }

    [Fact]
    public async Task TryReportGogPlaytimeSessionAsync_ReportsCompletedTrackedSession()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateGogItem("GOG playtime game", temp.GetPath("game"));
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var playtimeService = new StubGogPlaytimeSyncService();
        var launcher = CreateLauncherWithComet(
            temp,
            settings,
            new StubGogCometSessionService(),
            playtimeService);
        var startedAt = new DateTimeOffset(2026, 10, 25, 12, 30, 45, TimeSpan.Zero);
        var duration = TimeSpan.FromMinutes(12);

        await launcher.TryReportGogPlaytimeSessionAsync(
            item,
            recordStatistics: true,
            wasSessionTracked: true,
            startedAt,
            duration);

        var report = Assert.Single(playtimeService.Reports);
        Assert.Same(item, report.Item);
        Assert.Equal(startedAt, report.StartedAtUtc);
        Assert.Equal(duration, report.Duration);
        Assert.Equal(1, playtimeService.SyncRequests);
    }

    [Fact]
    public async Task TryReportGogPlaytimeSessionAsync_QueueFailureDoesNotEscapeOrRequestSync()
    {
        using var temp = new TemporaryDirectory();
        var playtimeService = new StubGogPlaytimeSyncService
        {
            EnqueueFailure = new GogPlaytimeQueueException(GogPlaytimeQueueFailure.Write)
        };
        var launcher = CreateLauncherWithComet(temp,
            new AppSettings { EnableGogGalaxyIntegration = true },
            new StubGogCometSessionService(), playtimeService);

        await launcher.TryReportGogPlaytimeSessionAsync(
            CreateGogItem("GOG game", temp.GetPath("game")),
            recordStatistics: true, wasSessionTracked: true,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        Assert.Single(playtimeService.Reports);
        Assert.Equal(0, playtimeService.SyncRequests);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotReportGogPlaytimeForTestLaunch()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var scriptPath = temp.CreateFile("gog-test-launch.sh", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = CreateGogItem("GOG test launch", scriptPath);
        var settings = new AppSettings { EnableGogGalaxyIntegration = true };
        var playtimeService = new StubGogPlaytimeSyncService();
        var launcher = CreateLauncherWithComet(
            temp,
            settings,
            new StubGogCometSessionService(),
            playtimeService);

        await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.Empty(playtimeService.Reports);
        Assert.Equal(0, playtimeService.SyncRequests);
    }

    private static MediaItem CreateGogItem(string title, string launchPath)
    {
        var item = new MediaItem(title)
        {
            MediaType = MediaType.Native,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = launchPath
                }
            ]
        };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123456";
        return item;
    }

    private static LauncherService CreateLauncherWithComet(
        TemporaryDirectory temp,
        AppSettings settings,
        IGogCometSessionService cometSessionService,
        IGogPlaytimeSyncService? playtimeSyncService = null)
    {
        var libraryRoot = temp.CreateDirectory("Library");
        return new LauncherService(
            libraryRoot,
            settings,
            new LaunchLogService(temp.GetPath("launch-logs")),
            new LaunchPlaylistService(libraryRoot),
            new LaunchEnvironmentService(),
            new WinePrefixService(libraryRoot, settings),
            new LaunchProcessService(),
            new ProtonPrefixRelocationService(libraryRoot, settings),
            new SteamProcessTrackingService(),
            cometSessionService,
            playtimeSyncService);
    }

    private sealed class SingleResultLegendaryProcessRunner(LegendaryCliResult result)
        : ILegendaryCliProcessRunner
    {
        public Task<LegendaryCliResult> RunAsync(
            LegendaryCliCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class StubGogCometSessionService : IGogCometSessionService
    {
        internal int StartCalls { get; private set; }
        internal StubGogCometGameSession Session { get; } = new();
        internal Exception? StartException { get; init; }
        internal TaskCompletionSource? StartBlocker { get; init; }
        internal TaskCompletionSource StartEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IGogCometGameSession?> TryStartAsync(
            MediaItem item,
            CancellationToken ct = default)
        {
            StartCalls++;
            StartEntered.TrySetResult();
            if (StartException != null)
                throw StartException;
            if (StartBlocker != null)
                await StartBlocker.Task.WaitAsync(ct);

            return Session;
        }
    }

    private sealed class StubGogCometGameSession : IGogCometGameSession
    {
        internal int CompleteCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool CompletionCancellationObserved { get; private set; }
        internal TaskCompletionSource? CompletionBlocker { get; set; }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            CompleteCalls++;
            if (CompletionBlocker != null)
            {
                try
                {
                    await CompletionBlocker.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    CompletionCancellationObserved = true;
                    throw;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubGogPlaytimeSyncService : IGogPlaytimeSyncService
    {
        internal List<ReportCall> Reports { get; } = [];
        internal int SyncRequests { get; private set; }
        internal Exception? EnqueueFailure { get; init; }

        public Task EnqueueSessionAsync(
            MediaItem item,
            DateTimeOffset startedAtUtc,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            Reports.Add(new ReportCall(item, startedAtUtc, duration));
            if (EnqueueFailure != null)
                return Task.FromException(EnqueueFailure);
            return Task.CompletedTask;
        }

        public void RequestPendingSync() => SyncRequests++;

        public Task TrySyncPendingAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed record ReportCall(
        MediaItem Item,
        DateTimeOffset StartedAtUtc,
        TimeSpan Duration);

    private sealed class StubSteamProcessTrackingService(TimeSpan duration)
        : SteamProcessTrackingService(
            "/proc",
            _ => { },
            enabled: false,
            observationDuration: TimeSpan.Zero,
            initialPollInterval: TimeSpan.Zero,
            steadyPollInterval: TimeSpan.Zero,
            quietPeriod: TimeSpan.Zero,
            candidateConfirmationDuration: TimeSpan.Zero)
    {
        internal int PrepareCalls { get; private set; }
        internal int TrackCalls { get; private set; }

        internal override SteamProcessTrackingSession? Prepare(uint appId, string? title)
        {
            PrepareCalls++;
            return new SteamProcessTrackingSession(
                1,
                appId,
                title ?? string.Empty,
                new HashSet<SteamProcessIdentity>());
        }

        internal override Task<SteamProcessTrackingResult> TrackAsync(
            SteamProcessTrackingSession? session,
            Action<IReadOnlyCollection<int>>? onGameProcessesDetected = null,
            CancellationToken cancellationToken = default)
        {
            TrackCalls++;
            return Task.FromResult(new SteamProcessTrackingResult(true, duration));
        }
    }

    private sealed class BlockingSteamProcessTrackingService()
        : SteamProcessTrackingService(
            "/proc",
            _ => { },
            enabled: false,
            observationDuration: TimeSpan.Zero,
            initialPollInterval: TimeSpan.Zero,
            steadyPollInterval: TimeSpan.Zero,
            quietPeriod: TimeSpan.Zero,
            candidateConfirmationDuration: TimeSpan.Zero)
    {
        internal override SteamProcessTrackingSession? Prepare(uint appId, string? title)
            => new(1, appId, title ?? string.Empty, new HashSet<SteamProcessIdentity>());

        internal override async Task<SteamProcessTrackingResult> TrackAsync(
            SteamProcessTrackingSession? session,
            Action<IReadOnlyCollection<int>>? onGameProcessesDetected = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SteamProcessTrackingResult.NotTracked;
        }
    }

    private static MediaItem CreateSteamCommandItem(TemporaryDirectory temp, string title)
    {
        var steamPath = temp.CreateFile(
            "steam",
            "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            steamPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new MediaItem(title)
        {
            MediaType = MediaType.Command,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = steamPath
                }
            ],
            LauncherArgs = "steam://rungameid/123"
        };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "steam";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
        return item;
    }

}
