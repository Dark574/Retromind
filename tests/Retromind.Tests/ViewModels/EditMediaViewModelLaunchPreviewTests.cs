using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
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
            new FileManagementService(AppPaths.LibraryRoot),
            new WinetricksService(AppPaths.LibraryRoot),
            [parent.Name],
            new UnexpectedIdentificationService(),
            rootNodes: new ObservableCollection<MediaNode> { parent },
            parentNode: parent);

        Assert.Equal(
            $"> /usr/bin/gamemoderun \"/opt/Inner  Wrapper/wrapper.sh\" \"{gamePath}\"",
            viewModel.PreviewText);
    }

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
