using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Gog;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallerWorkflowServiceTests
{
    [Theory]
    [InlineData("recovered", true, true, 2)]
    [InlineData("partial", false, null, 2)]
    [InlineData("failed-with-existing", false, null, 2)]
    [InlineData("reinstall", true, true, 1)]
    [InlineData("stale-reinstall", false, null, 2)]
    [InlineData("no-manifest", true, false, 1)]
    [InlineData("invalid-manifest", true, false, 1)]
    [InlineData("validation-disabled", true, null, 1)]
    [InlineData("native-inno", true, true, 1)]
    [InlineData("inno-multilingual", true, true, 1)]
    [InlineData("inno-missing-recovered", true, true, 2)]
    [InlineData("inno-damaged-recovered", true, true, 2)]
    [InlineData("inno-unconfirmed-missing", true, false, 1)]
    public async Task WindowsInstall_ValidatesFilesFromTheSelectedPackage(
        string scenario, bool success, bool? verified, int attempts)
    {
        using var temp = new TemporaryDirectory();
        var bin = temp.CreateDirectory("bin");
        using var environment = new EnvironmentVariableScope(("PATH", bin), ("RETROMIND_GOG_VERIFY_MD5", null));
        var install = temp.CreateDirectory("Game");
        var staging = temp.CreateDirectory(".retromind-gog-installers", "123", "windows");
        var setup = Path.Combine(staging, "setup.exe");
        var part = Path.Combine(staging, "setup-1.bin");
        const string manifestName = "goggame-123.hashdb";
        var hashDb = temp.GetPath(manifestName);
        var installedHashDb = Path.Combine(install, manifestName);
        var game = Path.Combine(install, "game.exe");
        var air = Path.Combine(install, "Adobe AIR.dll");
        File.WriteAllText(setup, "fixture");
        var innoFallback = scenario.StartsWith("inno-", StringComparison.Ordinal);
        if (scenario != "native-inno" && !innoFallback)
            File.WriteAllBytes(part, "Rar!\x1a\x07\0"u8.ToArray());
        File.WriteAllBytes(hashDb, scenario == "invalid-manifest" ? [0] :
            innoFallback ? GogPackageHashDbTests.BuildHashDb(manifestName) :
            scenario == "native-inno"
                ? GogPackageHashDbTests.BuildHashDb(manifestName, ("game.exe", "game"), ("Adobe AIR.dll", "air"))
                : GogPackageHashDbTests.BuildHashDb(manifestName, ("/game.exe", "game"), ("/Adobe AIR.dll", "air")));
        var size = new FileInfo(hashDb).Length;
        var archiveReader = temp.CreateFile("bin/7z", $$"""
            #!/bin/sh
            case "$1" in
              l)
                [ "$6" = '{{part}}' ] || exit 5
                /usr/bin/cat <<'INVENTORY'
            Path = game/game.exe
            Folder = -
            Size = 4

            Path = game/Adobe AIR.dll
            Folder = -
            Size = 3

            Path = game/{{manifestName}}
            Folder = -
            Size = {{size}}
            INVENTORY
                ;;
              x)
                [ "$5" = '{{part}}' ] || exit 5
                /usr/bin/cat '{{hashDb}}'
                ;;
              *) exit 5 ;;
            esac
            """);
        var wine = temp.CreateFile("bin/wine", $$"""
            #!/bin/sh
            silent=no
            for arg do
                case "$arg" in
                    /LOG=Z:*) log=$(printf '%s' "${arg#/LOG=Z:}" | /usr/bin/tr '\\' '/') ;;
                    /DIR=*) destination=${arg#/DIR=} ;;
                    /SILENT) silent=yes ;;
                esac
            done
            case '{{scenario}}' in
                failed-with-existing) exit 1 ;;
                reinstall|stale-reinstall) ;;
                *)
                    printf game > '{{game}}'
                    if [ '{{scenario}}' = native-inno ] || { [ '{{scenario}}' = recovered ] && [ "$silent" = no ]; }; then
                        printf air > '{{air}}'
                        /usr/bin/cp '{{hashDb}}' '{{installedHashDb}}'
                    fi
                    ;;
            esac
            : > "$log"
            case '{{scenario}}' in
                inno-*)
                    printf '2026-11-25 09:28:12.732   -- File entry --\n' >> "$log"
                    printf '2026-11-25 09:28:12.732   Dest filename: %s\\game.exe\n' "$destination" >> "$log"
                    printf '2026-11-25 09:28:12.732   Successfully installed the file.\n' >> "$log"
                    ;;
            esac
            case '{{scenario}}' in
                inno-multilingual) printf air > '{{air}}' ;;
                inno-missing-recovered|inno-damaged-recovered)
                    printf '2026-11-25 09:28:12.732   -- File entry --\n' >> "$log"
                    printf '2026-11-25 09:28:12.732   Dest filename: %s\\Adobe AIR.dll\n' "$destination" >> "$log"
                    printf '2026-11-25 09:28:12.732   Successfully installed the file.\n' >> "$log"
                    if [ "$silent" = no ]; then
                        printf air > '{{air}}'
                    elif [ '{{scenario}}' = inno-damaged-recovered ]; then
                        printf bad > '{{air}}'
                    fi
                    ;;
            esac
            printf '2026-11-25 09:29:12.732   Installation process succeeded.\n' >> "$log"
            printf '2026-11-25 09:29:12.758   Log closed.\n' >> "$log"
            """);
        foreach (var script in new[] { wine, archiveReader })
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (scenario == "native-inno")
        {
            var innoextract = temp.CreateFile("bin/innoextract", $$"""
                #!/bin/sh
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        --list-languages) exit 0 ;;
                        --list-sizes) printf 'app/content with spaces/en/\n4 game.exe\n3 Adobe AIR.dll\n{{size}} {{manifestName}}\n'; exit ;;
                        --output-dir) /usr/bin/cp '{{hashDb}}' "$2/{{manifestName}}"; exit ;;
                    esac
                    shift
                done
                exit 6
                """);
            File.SetUnixFileMode(innoextract, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (innoFallback)
        {
            var innoextract = temp.CreateFile("bin/innoextract", $$"""
                #!/bin/sh
                language=
                dump=no
                checksums=no
                omit_temp=no
                action=
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        --list-languages) printf 'de-DE German\nen-US English\nes-ES Spanish\nfr-FR French\npl-PL Polish\nru-RU Russian\n'; exit ;;
                        --language) language="$2"; shift ;;
                        --dump) dump=yes ;;
                        --list-checksums) checksums=yes ;;
                        --exclude-temp) omit_temp=yes ;;
                        --list-sizes) action=list ;;
                        --output-dir) /usr/bin/cp '{{hashDb}}' "$2/{{manifestName}}"; exit ;;
                    esac
                    shift
                done
                [ -n "$language" ] && [ "$action" = list ] && [ "$omit_temp" = yes ] || exit 2
                if [ "$checksums" = yes ] && [ "$dump" = yes ]; then
                    /usr/bin/cat <<'LIST'
                4 MD5 c8d46d341bea4fd5bff866a65ff8aea9 {app}\game.exe
                3 MD5 3c4588116394d2164657875b7430907e {app}\Adobe AIR.dll
                4 MD5 c8d46d341bea4fd5bff866a65ff8aea9 {tmp}\helper.exe
                4 MD5 c8d46d341bea4fd5bff866a65ff8aea9 {commonappdata}\GOG.com\uninstall.dll
                LIST
                else
                    printf '4 game.exe\n3 Adobe AIR.dll\n{{size}} {{manifestName}}\n'
                fi
                """);
            File.SetUnixFileMode(innoextract, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var save = temp.CreateFile("Game/save.dat", "existing save");
        if (scenario is "reinstall" or "stale-reinstall" or "failed-with-existing")
        {
            File.WriteAllText(game, "game");
            File.WriteAllText(air, scenario == "stale-reinstall" ? "bad" : "air");
            File.Copy(hashDb, installedHashDb);
        }

        var item = new MediaItem("Game") { Files = [new() { Path = game }] };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = install;
        GogInstallDirectorySafety.WriteMarker(install, item);
        var packagePaths = scenario is "no-manifest" or "native-inno" || innoFallback ? new[] { setup } : [setup, part];
        var package = new GogInstallerPackage("123", GogInstallPlatform.Windows, "Game", "1",
            packagePaths.Select(path => new GogInstallerDownloadFile(
                $"https://invalid.example/{Path.GetFileName(path)}", Path.GetFileName(path), new FileInfo(path).Length))
                .ToArray());
        var output = new ConcurrentQueue<string>();

        var result = await CreateService(temp.RootPath).RunAsync(new(item, "123", install, GogInstallPlatform.Windows,
            package, staging, GogWindowsInstallerPreference.AutoPrefer64, false, false, false, false, false,
            TrackBaseGameInstallation: true, ValidatePackageFiles: scenario != "validation-disabled"), output.Enqueue);

        Assert.True(result.Success == success, string.Join('\n', output));
        Assert.Equal(verified, result.PackageFilesVerified);
        Assert.Equal(attempts, output.Count(line => line.StartsWith("[Windows installer] Attempt ", StringComparison.Ordinal)));
        Assert.Equal("existing save", File.ReadAllText(save));
        // Only the successful UI flow may publish the final install marker.
        Assert.True(GogInstallDirectorySafety.IsIncomplete(install, item));
        if (!success)
            Assert.Equal(GogInstallerWorkflowFailureStage.Execution, result.FailureStage);
        if (scenario is "recovered" or "partial")
            Assert.Contains(output, line => line.Contains("Required package file is missing", StringComparison.Ordinal) &&
                line.Contains("Adobe AIR.dll", StringComparison.Ordinal));
        if (scenario == "stale-reinstall")
            Assert.Contains("wrong MD5", result.ErrorMessage);
        if (scenario == "inno-missing-recovered")
            Assert.Contains(output, line => line.Contains("Required package file is missing: 'Adobe AIR.dll'."));
        if (scenario == "inno-damaged-recovered")
            Assert.Contains(output, line => line.Contains("Required package file has the wrong size or checksum: 'Adobe AIR.dll'."));
        if (innoFallback && verified == true)
            Assert.Contains(output, line => line.Contains("Inno-Dateiliste: 2/2"));
        if (verified == false)
            Assert.Contains("[Package files] Installation completed. File integrity was not verified.", output);
        if (scenario == "no-manifest")
            Assert.Contains("[Package files] innoextract is not available in PATH.", output);
    }

    [Theory]
    [InlineData("GE-Proton")]
    [InlineData("Valve Proton")]
    public async Task WindowsInstall_UsesSystemWineAndSharesPortableProfileWithSelectedProton(string runnerName)
    {
        using var temp = new TemporaryDirectory();
        var bin = temp.CreateDirectory("bin");
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("PATH", bin), ("USER", "linux-user"));
        var library = temp.CreateDirectory("Library");
        var install = temp.CreateDirectory("Library", "Games", "Game");
        var staging = temp.CreateDirectory("staging");
        var installer = temp.CreateFile("staging/setup.exe", "fixture");
        var runnerRoot = temp.CreateDirectory(runnerName);
        var wine = temp.CreateFile("bin/wine", """
            #!/bin/sh
            [ "$USER" = steamuser ] || exit 2
            [ -d "$WINEPREFIX/dosdevices/c:" ] && [ -d "$WINEPREFIX/dosdevices/z:" ] || exit 3
            printf 'save' > "$WINEPREFIX/drive_c/users/steamuser/Documents/setup-save"
            printf 'installed' > "$WINEPREFIX/dosdevices/r:/Game/game.exe"
            printf 'installer used system wine\n'
            """);
        var umu = temp.CreateFile("bin/umu-run", """
            #!/bin/sh
            [ "$USER" = linux-user ] || exit 2
            [ -d "$PROTONPATH" ] || exit 3
            [ "$WINEPREFIX" = "$STEAM_COMPAT_DATA_PATH" ] || exit 4
            [ -f "$WINEPREFIX/pfx/drive_c/users/steamuser/Documents/setup-save" ] || exit 5
            [ -f "$WINEPREFIX/pfx/dosdevices/r:/Game/game.exe" ] || exit 6
            printf 'runner saw installer profile\n'
            exit 23
            """);
        foreach (var script in new[] { wine, umu })
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var item = new MediaItem("Game")
        {
            MediaType = MediaType.Emulator,
            LauncherPath = umu,
            PrefixPath = Path.Combine("Prefixes", "Game"),
            RunnerVersionId = runnerName,
            Files = [new() { Path = Path.Combine(install, "game.exe") }]
        };
        var package = new GogInstallerPackage("123", GogInstallPlatform.Windows, "Game", "1",
            [new("https://invalid.example/setup.exe", "setup.exe", new FileInfo(installer).Length)]);
        var output = new ConcurrentQueue<string>();

        var result = await CreateService(temp.RootPath).RunAsync(new(item, "123", install, GogInstallPlatform.Windows,
            package, staging, GogWindowsInstallerPreference.AutoPrefer64, true, true, false, false, false),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("installer used system wine", output);
        Assert.Equal(runnerName, item.RunnerVersionId);
        var launcher = new LauncherService(library, new AppSettings(), new LaunchLogService(temp.GetPath("launch-logs")));
        var launch = await launcher.LaunchAsync(item, environmentOverrides: new Dictionary<string, string>
        {
            ["PROTONPATH"] = runnerRoot
        }, recordStatistics: false);

        Assert.Equal(23, launch.ExitCode);
        Assert.Contains("runner saw installer profile", launch.ConsoleOutput);
        Assert.Equal(runnerName, item.RunnerVersionId);
    }

    [Theory]
    [InlineData("success", null)]
    [InlineData("signal", "code 139")]
    [InlineData("error", "code 4")]
    [InlineData("truncated-log", "log does not confirm")]
    [InlineData("wine-crash", "crashed (exit code 0)")]
    [InlineData("fallback-success", null)]
    [InlineData("fallback-no-change", "did not place or update files")]
    public async Task WindowsInstall_RequiresSuccessfulSetupAndRetainsLogs(string scenario, string? expectedError)
    {
        using var temp = new TemporaryDirectory();
        var bin = temp.CreateDirectory("bin");
        using var environment = new EnvironmentVariableScope(("PATH", bin));
        var install = temp.CreateDirectory("Game");
        var staging = temp.CreateDirectory(".retromind-gog-installers", "123", "windows");
        var installer = Path.Combine(staging, "setup.exe");
        File.WriteAllText(installer, "fixture");
        var save = temp.CreateFile("Game/save.dat", "existing save");
        var partial = temp.GetPath("Game/game.exe");
        var wine = temp.CreateFile("bin/wine", $$"""
            #!/bin/sh
            ulimit -c 0
            silent=no
            for arg do
                case "$arg" in
                    /LOG=Z:*) log=$(printf '%s' "${arg#/LOG=Z:}" | /usr/bin/tr '\\' '/') ;;
                    /SILENT) silent=yes ;;
                esac
            done
            printf 'Wine setup stderr (%s)\n' "$silent" >&2
            if [ '{{scenario}}' != fallback-no-change ] || [ "$silent" = yes ]; then
                printf 'partial payload (%s)' "$silent" > '{{partial}}'
            fi
            printf '2026-11-25 08:27:00.000   Starting the installation process.\n' > "$log"
            case '{{scenario}}' in
                signal) kill -s SEGV "$$" ;;
                error) exit 4 ;;
                truncated-log) exit 0 ;;
                wine-crash) echo 'wine: Unhandled page fault on read access' >&2; exit 0 ;;
                fallback-*) [ "$silent" = no ] || exit 4 ;;
            esac
            printf '2026-11-25 08:27:02.000   Installation process succeeded.\n' >> "$log"
            printf '2026-11-25 08:27:02.001   Log closed.\n' >> "$log"
            """);
        File.SetUnixFileMode(wine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var item = new MediaItem("Game") { Files = [new() { Path = partial }] };
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123";
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = install;
        GogInstallDirectorySafety.WriteMarker(install, item);
        var package = new GogInstallerPackage("123", GogInstallPlatform.Windows, "Game", "1",
            [new("https://invalid.example/setup.exe", "setup.exe", new FileInfo(installer).Length)]);
        var output = new ConcurrentQueue<string>();

        var result = await CreateService(temp.RootPath).RunAsync(new(item, "123", install, GogInstallPlatform.Windows,
            package, staging, GogWindowsInstallerPreference.AutoPrefer64, false, false, false, false, false,
            TrackBaseGameInstallation: true), output.Enqueue);

        Assert.Equal(expectedError == null, result.Success);
        var fallbackExpected = scenario != "success";
        Assert.Equal(fallbackExpected, output.Any(line => line.Contains("Starting interactive fallback", StringComparison.Ordinal)));
        Assert.Contains("Wine setup stderr (yes)", output);
        Assert.Equal("existing save", File.ReadAllText(save));
        var lastWriter = fallbackExpected && scenario != "fallback-no-change" ? "no" : "yes";
        Assert.Equal($"partial payload ({lastWriter})", File.ReadAllText(partial));
        Assert.Equal(GogInstallDirectoryStatus.OwnedDirectory,
            GogInstallDirectorySafety.Assess(install, item, rejectSymbolicLinks: true).Status);
        Assert.True(GogInstallDirectorySafety.IsIncomplete(install, item));
        Assert.False(GogMediaItemStateHelper.IsInstalled(item));
        Assert.True(GogMediaItemStateHelper.ShouldOfferInstall(item));
        if (expectedError == null)
        {
            // The library workflow writes the final marker after resolving the executable.
            GogInstallDirectorySafety.WriteMarker(install, item);
            Assert.True(GogMediaItemStateHelper.IsInstalled(item));
        }
        else
        {
            Assert.Equal(GogInstallerWorkflowFailureStage.Execution, result.FailureStage);
            Assert.Contains(expectedError, result.ErrorMessage);
            Assert.Contains("may contain an incomplete installation", result.ErrorMessage);
        }

        GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(result.DownloadedPackage!.StagingDirectory);
        Assert.False(Directory.Exists(staging));
        var logDirectory = Assert.Single(Directory.GetDirectories(temp.GetPath("installer-logs")));
        var logs = Directory.GetFiles(logDirectory, "inno-setup-*.log");
        Assert.Equal(fallbackExpected ? 2 : 1, logs.Length);
        Assert.All(logs, path => Assert.Contains("Starting the installation process.", File.ReadAllText(path)));
        if (expectedError != null)
            Assert.Contains(expectedError, File.ReadAllText(Path.Combine(logDirectory, "retromind-install.log")));
        Assert.Contains("Wine setup stderr (yes)", File.ReadAllText(Path.Combine(logDirectory, "retromind-install.log")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_ReusesDownloadAndHandlesSelectedArchive(bool keepInstaller, bool archiveBlocked)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temporaryDirectory.GetPath("Retromind.AppImage")));
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Workflow Game");
        var stagingPath = temporaryDirectory.CreateDirectory(
            "Library",
            "Games",
            "Workflow Game",
            ".retromind-gog-installers",
            "123",
            "linux");
        var installerPath = CreateInstallerScript(stagingPath);
        var package = new GogInstallerPackage(
            "123",
            GogInstallPlatform.Linux,
            "Workflow installer",
            "1.0",
            [new GogInstallerDownloadFile(
                "https://invalid.example/installer.sh",
                Path.GetFileName(installerPath),
                new FileInfo(installerPath).Length)]);
        var item = new MediaItem { Id = "workflow-item", Title = "Workflow Game" };
        var output = new ConcurrentQueue<string>();
        var service = CreateService(temporaryDirectory.RootPath);
        var settings = new DownloadSettings
        {
            KeepGogInstallers = keepInstaller,
            GogArchiveDirectory = archiveBlocked ? temporaryDirectory.GetPath("blocked-archive") : null
        };
        if (settings.GogArchiveDirectory is { } blockedPath)
            File.WriteAllText(blockedPath, "keep");

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "123",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: false,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true,
                DownloadSettings: settings),
            output.Enqueue);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.DownloadedPackage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.Single(Directory.GetFiles(temporaryDirectory.GetPath("installer-logs"),
            "retromind-install.log", SearchOption.AllDirectories));
        Assert.Contains(output, line => line.Contains("Validating 1/1", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("Detailed log file:", StringComparison.Ordinal));
        Assert.Equal(archiveBlocked, result.ArchivePending);
        if (archiveBlocked)
        {
            Assert.True(File.Exists(installerPath));
            Assert.Equal("keep", File.ReadAllText(settings.GogArchiveDirectory!));
            return;
        }

        var archive = GogInstallerArchive.GetPackageDirectory(settings, package, "123", item.Title);
        var archivedFile = Path.Combine(archive, Path.GetFileName(installerPath));
        Assert.Equal(keepInstaller, File.Exists(archivedFile));
        if (keepInstaller)
        {
            Assert.Equal(File.ReadAllText(installerPath), File.ReadAllText(archivedFile));
            GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(result.DownloadedPackage.StagingDirectory);
            Assert.False(Directory.Exists(stagingPath));
            Assert.True(File.Exists(archivedFile));
            Assert.False(File.Exists(Path.Combine(archive, "retromind-install.log")));
        }
    }

    [Fact]
    public async Task RunAsync_ManualMainGameAndDlcShareArchiveFolder()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var installPath = temp.CreateDirectory("Library", "Games", "Manual Game");
        var item = new MediaItem("Manual Game");
        var settings = new DownloadSettings { KeepGogInstallers = true };
        var service = CreateService(temp.RootPath);
        foreach (var dlc in new[] { false, true })
        {
            var staging = temp.CreateDirectory(dlc ? "dlc-staging" : "main-staging");
            var source = CreateInstallerScript(staging);
            if (dlc)
            {
                var renamed = Path.Combine(staging, "dlc.sh");
                File.Move(source, renamed);
                source = renamed;
                File.WriteAllText(source, File.ReadAllText(source).Replace("game.x86_64", "dlc-data.bin"));
            }
            var package = new GogInstallerPackage(dlc ? "456" : "123", GogInstallPlatform.Linux,
                dlc ? "DLC" : "Game", "1", [new("https://invalid.example/installer", Path.GetFileName(source),
                    new FileInfo(source).Length)]);
            var result = await service.RunAsync(new GogInstallerWorkflowRequest(item, "123", installPath,
                GogInstallPlatform.Linux, package, staging, GogWindowsInstallerPreference.AutoPrefer64,
                false, false, false, UseTemporaryLinuxDestination: !dlc, RequireLinuxPayloadChange: true,
                DownloadSettings: settings), _ => { });
            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(result.ArchivePending);
            GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(staging);
        }
        var archive = Assert.Single(Directory.GetDirectories(GogInstallerArchive.DefaultRoot));
        Assert.Contains("Manual_Game", Path.GetFileName(archive));
        Assert.Equal(["dlc.sh", "installer.sh"], Directory.GetFiles(archive)
            .Select(Path.GetFileName).Where(name => !name!.StartsWith('.')).Order());
        Assert.Empty(Directory.GetDirectories(archive));
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.True(File.Exists(Path.Combine(installPath, "dlc-data.bin")));
    }

    [Fact]
    public async Task RunAsync_CleanInstallPreservesNestedStagingAndRemovesOldPayload()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Clean Workflow Game");
        var stagingPath = temporaryDirectory.CreateDirectory(
            "Library",
            "Games",
            "Clean Workflow Game",
            ".retromind-gog-installers",
            "456",
            "linux");
        var oldPayloadPath = Path.Combine(installPath, "old-payload.bin");
        File.WriteAllText(oldPayloadPath, "old");
        var installerPath = CreateInstallerScript(stagingPath);
        var package = new GogInstallerPackage(
            "456",
            GogInstallPlatform.Linux,
            "Clean workflow installer",
            "2.0",
            [new GogInstallerDownloadFile(
                "https://invalid.example/installer.sh",
                Path.GetFileName(installerPath),
                new FileInfo(installerPath).Length)]);
        var item = new MediaItem { Id = "clean-workflow-item", Title = "Clean Workflow Game" };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "456";
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        var service = CreateService(temporaryDirectory.RootPath);

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "456",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: true,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true),
            _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(File.Exists(oldPayloadPath));
        Assert.True(File.Exists(installerPath));
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
    }

    [Fact]
    public async Task RunAsync_CleanWindowsInstallDoesNotDeleteTheConfirmedPrefix()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("PATH", temp.CreateDirectory("no-runners")));
        var install = temp.CreateDirectory("Library", "Games", "Game");
        var prefix = temp.CreateDirectory("Library", "Games", "Game", "prefix");
        var save = temp.CreateFile("Library/Games/Game/prefix/drive_c/save.dat", "keep");
        var oldGame = temp.CreateFile("Library/Games/Game/game.exe");
        var staging = temp.CreateDirectory("staging");
        var installer = temp.CreateFile("staging/setup.exe", "installer");
        var item = new MediaItem("Game") { Id = "game-item" };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "123";
        GogInstallDirectorySafety.WriteMarker(install, item);
        var package = new GogInstallerPackage("123", GogInstallPlatform.Windows, "Game", "1",
            [new("https://invalid.example/setup.exe", "setup.exe", new FileInfo(installer).Length)]);
        var selected = WinePrefixService.ResolveExplicitOverride(WinePrefixRuntime.Wine,
            new Dictionary<string, string> { ["WINEPREFIX"] = prefix }, out var specified);
        Assert.True(specified);
        Assert.NotNull(selected);

        var result = await CreateService(temp.RootPath).RunAsync(new GogInstallerWorkflowRequest(item, "123",
            install, GogInstallPlatform.Windows, package, staging, GogWindowsInstallerPreference.AutoPrefer64,
            false, false, CleanInstall: true, UseTemporaryLinuxDestination: false,
            RequireLinuxPayloadChange: false, ResolvedPrefix: selected), _ => { });

        Assert.False(result.Success);
        Assert.Contains("clean installation would delete it", result.ErrorMessage);
        Assert.Equal("keep", await File.ReadAllTextAsync(save));
        Assert.True(File.Exists(oldGame));
    }

    [Fact]
    public async Task RunAsync_CancelledDownloadReturnsCancelledStage()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var installPath = temporaryDirectory.CreateDirectory("Library", "Games", "Cancelled Workflow Game");
        var stagingPath = Path.Combine(installPath, ".retromind-gog-installers", "789", "linux");
        var package = new GogInstallerPackage(
            "789",
            GogInstallPlatform.Linux,
            "Cancelled workflow installer",
            "1.0",
            [new GogInstallerDownloadFile("https://invalid.example/installer.sh", "installer.sh", 10)]);
        var item = new MediaItem { Id = "cancelled-workflow-item", Title = "Cancelled Workflow Game" };
        var service = CreateService(temporaryDirectory.RootPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.RunAsync(
            new GogInstallerWorkflowRequest(
                item,
                "789",
                installPath,
                GogInstallPlatform.Linux,
                package,
                stagingPath,
                GogWindowsInstallerPreference.AutoPrefer64,
                CreateDesktopShortcut: false,
                CreateStartMenuShortcuts: false,
                CleanInstall: false,
                UseTemporaryLinuxDestination: true,
                RequireLinuxPayloadChange: true),
            _ => { },
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(GogInstallerWorkflowFailureStage.Cancelled, result.FailureStage);
    }

    [Fact]
    public void DeleteStagingDirectoryBestEffort_OnLinuxRejectsDifferentlyCasedMarker()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stagingPath = temporaryDirectory.CreateDirectory(
            "game",
            ".RETROMIND-GOG-INSTALLERS",
            "123");

        GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(stagingPath);

        Assert.True(Directory.Exists(stagingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ManualUpdateAndCleanReinstallReuseTheQueuedInstaller(bool cleanInstall)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var installPath = temp.CreateDirectory("Library", "Games", "Queued Game");
        var source = CreateInstallerScript(temp.CreateDirectory("source"));
        var package = new GogInstallerPackage("123", GogInstallPlatform.Linux, "Queued Game", "2.0",
            [new GogInstallerDownloadFile("https://invalid.example/installer.sh", "installer.sh", new FileInfo(source).Length)]);
        var cache = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        File.Copy(source, Path.Combine(cache, "installer.sh"));
        var staging = Path.Combine(installPath, ".retromind-gog-installers", "123", "linux");
        var oldFile = Path.Combine(installPath, "old-game.bin");
        File.WriteAllText(oldFile, "old");
        using var client = new HttpClient(new RejectDownloadsHandler());
        var service = CreateService(temp.RootPath, client);
        var item = new MediaItem("Queued Game") { Id = "queued-game" };
        item.CustomFields["Store.ProviderId"] = "gog";
        item.CustomFields["Store.GameId"] = "123";
        GogInstallDirectorySafety.WriteMarker(installPath, item);

        var result = await service.RunAsync(new GogInstallerWorkflowRequest(
                item, "123", installPath,
                GogInstallPlatform.Linux, package, staging, GogWindowsInstallerPreference.AutoPrefer64,
                false, false, cleanInstall, UseTemporaryLinuxDestination: true, RequireLinuxPayloadChange: true),
            _ => { });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.Equal(!cleanInstall, File.Exists(oldFile));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(result.DownloadedPackage!.EntryFilePath));
    }

    [Fact]
    public async Task PausedQueue_InstallsCachedBaseGameAndDownloadsItsDlcWhileOtherJobsStayHeld()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        var installPath = temp.CreateDirectory("Library", "Games", "Game");
        var item = new MediaItem("Game");
        var source = CreateInstallerScript(temp.CreateDirectory("source"));
        var package = new GogInstallerPackage("123", GogInstallPlatform.Linux, "Game", "1.0",
            [new GogInstallerDownloadFile("https://invalid.example/installer.sh", "installer.sh", new FileInfo(source).Length)]);
        var cache = Directory.CreateDirectory(GogInstallerArchive.GetStagingDirectory(package)).FullName;
        File.Copy(source, Path.Combine(cache, "installer.sh"));
        var dlcBytes = Encoding.UTF8.GetBytes(File.ReadAllText(source).Replace("game.x86_64", "dlc-data.bin"));
        var dlc = new GogInstallerPackage("456", GogInstallPlatform.Linux, "DLC", "1.0",
            [new GogInstallerDownloadFile("https://invalid.example/dlc.sh", "dlc.sh", dlcBytes.Length)]);
        var handler = new DlcDownloadHandler(dlcBytes);
        using var client = new HttpClient(handler);
        var service = CreateService(temp.RootPath, client);
        var revision = GogInstallerArchive.GetRevision(package);
        var settings = new DownloadSettings { KeepGogInstallers = true };
        var install = new DownloadJob(Guid.NewGuid(), item.Id, DownloadStore.Gog, DownloadKind.Update,
            revision, false, false, false, DownloadState.InstallationPending, PayloadReady: true, PayloadRevision: revision);
        var waiting = install with
        {
            Id = Guid.NewGuid(), ItemId = "other-game", State = DownloadState.Paused,
            PayloadReady = false, PayloadRevision = null
        };
        var path = temp.GetPath("downloads.json");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([install, waiting]);
        }
        var errors = new RecordingErrorService();
        var manager = new DownloadManagerService(path, errors, TimeProvider.System);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job.Id == install.Id && row.Job.State is
                    DownloadState.Completed or DownloadState.Failed or DownloadState.RepairRequired))
                settled.TrySetResult();
        };
        GogInstallerWorkflowResult? baseResult = null;
        GogInstallerWorkflowResult? dlcResult = null;
        manager.Start(new(), async (work, ct) =>
        {
            Assert.Equal(install.Id, work.Job.Id);
            Assert.True(work.BeginInstallation());
            baseResult = await service.RunAsync(new GogInstallerWorkflowRequest(
                    item, "123", installPath, GogInstallPlatform.Linux, package,
                    Path.Combine(installPath, ".retromind-gog-installers", "123", "linux"),
                    GogWindowsInstallerPreference.AutoPrefer64, false, false, false,
                    UseTemporaryLinuxDestination: true, RequireLinuxPayloadChange: true, RequireCachedPayload: true,
                    DownloadSettings: settings),
                _ => { }, ct);
            Assert.True(baseResult.Success, baseResult.ErrorMessage);

            // Automatic DLC reapplication belongs to the explicit install operation, not a new queue job.
            dlcResult = await service.RunAsync(new GogInstallerWorkflowRequest(
                item, "123", installPath, GogInstallPlatform.Linux, dlc,
                Path.Combine(installPath, ".retromind-gog-installers", "dlc", "456", "linux"),
                GogWindowsInstallerPreference.AutoPrefer64, false, false, false,
                UseTemporaryLinuxDestination: false, RequireLinuxPayloadChange: true, DownloadSettings: settings),
                _ => { }, ct);
            Assert.True(dlcResult.Success, dlcResult.ErrorMessage);
            Assert.True(manager.IsQueuePaused);
            return new DownloadOutcome(DownloadState.Completed);
        });
        try
        {
            manager.ToggleQueuePause();
            Assert.True(manager.Change(waiting.Id, DownloadAction.Resume));
            Assert.True(manager.Change(install.Id, DownloadAction.Install));
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await manager.StopAsync(); manager.Release(); }

        Assert.True(baseResult?.Success, baseResult?.ErrorMessage);
        Assert.True(dlcResult?.Success, dlcResult?.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(installPath, "game.x86_64")));
        Assert.True(File.Exists(Path.Combine(installPath, "dlc-data.bin")));
        var archive = GogInstallerArchive.GetPackageDirectory(settings, package, "123", item.Title);
        Assert.Equal(["dlc.sh", "installer.sh"], Directory.GetFiles(archive)
            .Select(Path.GetFileName).Where(name => !name!.StartsWith('.')).Order());
        Assert.Empty(Directory.GetDirectories(archive));
        Assert.Single(Directory.GetDirectories(GogInstallerArchive.DefaultRoot));
        Assert.Equal(1, handler.Requests);
        Assert.True(manager.IsQueuePaused);
        Assert.Equal(DownloadState.Completed, manager.Snapshot()[0].Job.State);
        Assert.Equal(DownloadState.Queued, manager.Snapshot()[1].Job.State);
        Assert.Empty(errors.Reports);
    }

    private sealed class DlcDownloadHandler(byte[] installer) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/dlc.sh", request.RequestUri!.AbsolutePath);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        }
    }

    private sealed class RejectDownloadsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("The queued installer must be reused without network transfers.");
    }

    private static string CreateInstallerScript(string stagingPath)
    {
        var path = Path.Combine(stagingPath, "installer.sh");
        File.WriteAllText(
            path,
            """
            #!/bin/sh
            destination=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--destination" ]; then
                shift
                destination="$1"
              fi
              shift
            done
            [ -n "$destination" ] || exit 9
            mkdir -p "$destination"
            printf 'game' > "$destination/game.x86_64"
            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static GogInstallerWorkflowService CreateService(string rootPath, HttpClient? downloads = null)
    {
        var installService = new GogInstallService(
            new GogAuthService(
                new InMemorySecretStore(),
                new GogOAuthClient(new HttpClient()),
                new GogPkceService(), new RecordingErrorService()),
            new HttpClient(),
            downloadHttpClient: downloads ?? new HttpClient(new RejectDownloadsHandler()));
        var executionService = new GogInstallerExecutionService(
            new GogInstallerProcessService(),
            Path.Combine(rootPath, "Library"));
        return new GogInstallerWorkflowService(
            installService,
            executionService,
            new ProtonPrefixRelocationService(
                Path.Combine(rootPath, "Library"),
                new AppSettings()),
            Path.Combine(rootPath, "installer-logs"));
    }
}
