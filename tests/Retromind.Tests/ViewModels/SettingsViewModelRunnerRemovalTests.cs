using System.Collections.ObjectModel;
using System.Net;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelRunnerRemovalTests
{
    [Fact]
    public async Task RemoveManagedRunner_WhenAssignmentSaveFails_KeepsRegistrationAndFiles()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var runnerDirectory = CreateManagedRunner(temp, "GE-Proton10-1");
        var settings = CreateSettings();
        var roots = CreateLibraryWithRunnerAssignment("managed");
        var settingsService = new SettingsService();
        using var runnerService = new RunnerVersionService(temp.RootPath);
        using var viewModel = CreateViewModel(settings, settingsService, runnerService, roots);
        SelectManagedRunnerAndReplacement(viewModel);

        var filesExistedDuringPersistence = false;
        var registrationExistedDuringPersistence = false;
        viewModel.RequestRunnerVersionRemovalConfirmation += _ => Task.FromResult(true);
        viewModel.RequestRunnerVersionAssignmentPersistence += () =>
        {
            filesExistedDuringPersistence = Directory.Exists(runnerDirectory);
            registrationExistedDuringPersistence = settings.RunnerVersions.Any(r => r.Id == "managed");
            return Task.FromResult(false);
        };

        await viewModel.RemoveRunnerVersionCommand.ExecuteAsync(null);

        Assert.True(filesExistedDuringPersistence);
        Assert.True(registrationExistedDuringPersistence);
        Assert.True(Directory.Exists(runnerDirectory));
        Assert.Contains(viewModel.RunnerVersions, runner => runner.Id == "managed");
        Assert.Contains(settings.RunnerVersions, runner => runner.Id == "managed");
    }

    [Fact]
    public async Task RemoveManagedRunner_PersistsAssignmentsAndRegistrationBeforeDeletingFiles()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var runnerDirectory = CreateManagedRunner(temp, "GE-Proton10-1");
        var settings = CreateSettings();
        var roots = CreateLibraryWithRunnerAssignment("managed");
        var item = Assert.Single(roots[0].Items);
        var settingsService = new SettingsService();
        await settingsService.SaveAsync(settings);
        using var runnerService = new RunnerVersionService(temp.RootPath);
        using var viewModel = CreateViewModel(settings, settingsService, runnerService, roots);
        SelectManagedRunnerAndReplacement(viewModel);

        var filesExistedDuringPersistence = false;
        viewModel.RequestRunnerVersionRemovalConfirmation += _ => Task.FromResult(true);
        viewModel.RequestRunnerVersionAssignmentPersistence += async () =>
        {
            filesExistedDuringPersistence = Directory.Exists(runnerDirectory);
            await settingsService.SaveAsync(settings);
            return true;
        };

        await viewModel.RemoveRunnerVersionCommand.ExecuteAsync(null);

        var persistedSettings = await settingsService.LoadAsync();
        Assert.True(filesExistedDuringPersistence);
        Assert.False(Directory.Exists(runnerDirectory));
        Assert.Equal("replacement", item.RunnerVersionId);
        Assert.DoesNotContain(viewModel.RunnerVersions, runner => runner.Id == "managed");
        Assert.DoesNotContain(settings.RunnerVersions, runner => runner.Id == "managed");
        Assert.DoesNotContain(persistedSettings.RunnerVersions, runner => runner.Id == "managed");
    }

    private static AppSettings CreateSettings()
        => new()
        {
            RunnerVersions =
            [
                new RunnerVersionConfig
                {
                    Id = "managed",
                    Name = "GE-Proton10-1",
                    Kind = RunnerVersionKind.Proton,
                    SourceType = RunnerVersionSourceType.ManagedDownload,
                    Path = "Emulators/ProtonVersions/GE-Proton10-1"
                },
                new RunnerVersionConfig
                {
                    Id = "replacement",
                    Name = "GE-Proton10-2",
                    Kind = RunnerVersionKind.Proton,
                    SourceType = RunnerVersionSourceType.ExternalPath,
                    Path = "/opt/ge-proton-10-2"
                }
            ]
        };

    private static ObservableCollection<MediaNode> CreateLibraryWithRunnerAssignment(string runnerId)
    {
        var root = new MediaNode { Name = "Games" };
        root.Items.Add(new MediaItem
        {
            Title = "Test Game",
            MediaType = MediaType.Emulator,
            RunnerVersionId = runnerId
        });
        return [root];
    }

    private static string CreateManagedRunner(TemporaryDirectory temp, string name)
    {
        var directory = temp.CreateDirectory("Emulators", "ProtonVersions", name);
        temp.CreateFile($"Emulators/ProtonVersions/{name}/proton", "runner");
        temp.CreateFile($"Emulators/ProtonVersions/{name}/toolmanifest.vdf", "manifest");
        return directory;
    }

    private static void SelectManagedRunnerAndReplacement(SettingsViewModel viewModel)
    {
        viewModel.SelectedRunnerVersion = Assert.Single(
            viewModel.RunnerVersions,
            runner => runner.Id == "managed");
        viewModel.SelectedRunnerReplacement = Assert.Single(
            viewModel.RunnerReplacementOptions,
            replacement => replacement.Id == "replacement");
    }

    private static SettingsViewModel CreateViewModel(
        AppSettings settings,
        SettingsService settingsService,
        RunnerVersionService runnerService,
        ObservableCollection<MediaNode> roots)
    {
        var httpClient = new HttpClient(new StubHandler());
        var accountService = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            new InMemorySecretStore());
        return new SettingsViewModel(
            settings,
            settingsService,
            accountService,
            runnerService,
            roots);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
