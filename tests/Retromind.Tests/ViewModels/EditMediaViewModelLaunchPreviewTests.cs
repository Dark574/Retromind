using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class EditMediaViewModelLaunchPreviewTests
{
    [Fact]
    public void PreviewText_QuotesNestedWrapperPathAndPreservesConsecutiveSpaces()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var gamePath = temp.CreateFile(Path.Combine("Game  Files", "game.sh"));
        var item = new MediaItem("Wrapped game")
        {
            MediaType = MediaType.Native,
            NativeWrappersOverride =
            [
                new LaunchWrapper { Path = "/usr/bin/gamemoderun" },
                new LaunchWrapper { Path = "/opt/Inner  Wrapper/wrapper.sh" }
            ],
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = gamePath
                }
            ]
        };
        var parent = new MediaNode { Name = "Games" };
        parent.Items.Add(item);
        using var viewModel = new EditMediaViewModel(
            item,
            new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService()),
            new WinetricksService(AppPaths.LibraryRoot, new RecordingErrorService()),
            [parent.Name],
            new UnexpectedIdentificationService(),
            rootNodes: new ObservableCollection<MediaNode> { parent },
            parentNode: parent);

        Assert.Contains($"cd '{Path.GetDirectoryName(gamePath)}' && ", viewModel.PreviewText);
        Assert.EndsWith($"/usr/bin/gamemoderun \"/opt/Inner  Wrapper/wrapper.sh\" \"{gamePath}\"", viewModel.PreviewText);
    }

    [Fact]
    public async Task PreviewText_MatchesLaunchOfUnsavedArgumentsDirectoryAndEnvironmentAndRedactsSecrets()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var script = temp.CreateFile("game.sh", "#!/bin/sh\nprintf '%s|%s|%s|%s\\n' \"$PWD\" \"$PREVIEW_VISIBLE\" \"$XDG_CONFIG_HOME\" \"$*\"\nexit 23\n");
        var workingDirectory = temp.CreateDirectory("Custom directory");
        var xdg = temp.GetPath("Xdg directory");
        var item = new MediaItem("Game") { Files = [new() { Path = script }] };
        var node = new MediaNode("Games");
        node.Items.Add(item);
        var settings = new AppSettings();
        using var editor = CreateEditor(item, node, settings);
        editor.LauncherArgs = "--mode debug {file} --fullscreen --password argument-value";
        editor.WorkingDirectory = workingDirectory;
        editor.XdgConfigPath = xdg;
        editor.EnvironmentOverrides.Add(new EditMediaViewModel.EnvVarRow { Key = "PREVIEW_VISIBLE", Value = "from editor" });
        editor.EnvironmentOverrides.Add(new EditMediaViewModel.EnvVarRow { Key = "PREVIEW_API_TOKEN", Value = "environment-value" });

        var preview = editor.PreviewText;

        Assert.Contains($"cd '{workingDirectory}' && ", preview);
        Assert.Contains("PREVIEW_VISIBLE='from editor'", preview);
        Assert.Contains($"XDG_CONFIG_HOME='{xdg}'", preview);
        Assert.Contains("PREVIEW_API_TOKEN='<redacted>'", preview);
        Assert.EndsWith("--mode debug --fullscreen --password <redacted>", preview);
        Assert.DoesNotContain("argument-value", preview);
        Assert.DoesNotContain("environment-value", preview);
        Assert.Null(item.WorkingDirectory);
        await editor.SaveAndCloseCommand.ExecuteAsync(null);
        var launcher = new LauncherService(AppPaths.LibraryRoot, settings, new LaunchLogService(temp.GetPath("logs")));

        var result = await launcher.LaunchAsync(item, recordStatistics: false);

        Assert.Equal(LaunchOutcome.ExitedEarly, result.Outcome);
        Assert.Contains($"{workingDirectory}|from editor|{xdg}|--mode debug --fullscreen --password", result.ConsoleOutput);
    }

    [Fact]
    public void PreviewText_PlansPlaylistWithoutWritingItOrCreatingThePrefix()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var emulator = new EmulatorConfig { Id = "fixture", Path = "/bin/echo", Arguments = "{file}", UsePlaylistForMultiDisc = true };
        var settings = new AppSettings { Emulators = [emulator] };
        var item = new MediaItem("Multi disc")
        {
            MediaType = MediaType.Emulator, EmulatorId = emulator.Id,
            Files = [new() { Path = "/games/disc1.chd" }, new() { Path = "/games/disc2.chd" }]
        };
        var node = new MediaNode("Games");
        node.Items.Add(item);
        using var editor = CreateEditor(item, node, settings);
        editor.PrefixPath = "Prefixes/new";
        var expectedPlaylist = LaunchPlaylistService.GetPlannedPlaylistPath(item, [node.Name], AppPaths.LibraryRoot)!;

        var preview = editor.PreviewText;

        Assert.Contains(expectedPlaylist, preview);
        Assert.Contains(Strings.ResourceManager.GetString("Launch.Preview.PlaylistPlanned", Strings.Culture)!, preview);
        Assert.Contains($"WINEPREFIX={Path.Combine(AppPaths.LibraryRoot, "Prefixes/new")}", preview);
        Assert.Equal(Path.GetDirectoryName(expectedPlaylist), editor.EffectiveWorkingDirectory);
        Assert.False(File.Exists(expectedPlaylist));
        Assert.False(Directory.Exists(Path.Combine(AppPaths.LibraryRoot, "Prefixes/new")));
    }

    [Fact]
    public async Task PreviewText_UsesCommandLaunchRulesForExistingSteamCommands()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var item = new MediaItem("Steam game")
        {
            MediaType = MediaType.Command, LauncherArgs = "steam://rungameid/123",
            Files = [new() { Path = "steam" }]
        };
        var node = new MediaNode("Games");
        node.Items.Add(item);
        using var editor = CreateEditor(item, node, new AppSettings());

        Assert.EndsWith("steam -silent steam://rungameid/123", editor.PreviewText);
        await editor.SaveAndCloseCommand.ExecuteAsync(null);
        Assert.Equal(MediaType.Command, item.MediaType);
    }

    private static EditMediaViewModel CreateEditor(MediaItem item, MediaNode node, AppSettings settings) =>
        new(item, settings, new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService()),
            new WinetricksService(AppPaths.LibraryRoot, new RecordingErrorService()), [node.Name],
            new UnexpectedIdentificationService(),
            rootNodes: new ObservableCollection<MediaNode> { node }, parentNode: node);

    private sealed class UnexpectedIdentificationService : IRetroAchievementsGameIdentificationService
    {
        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");
    }
}
