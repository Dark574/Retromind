using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class RunnerVersionEnvironmentHelperTests
{
    [Fact]
    public void TryFindAvailableRunnerVersion_ReturnsMissingDefinitionForStaleId()
    {
        var settings = new AppSettings();

        var available = RunnerVersionEnvironmentHelper.TryFindAvailableRunnerVersion(
            settings,
            "removed-runner",
            out var runner,
            out var resolvedPath);

        Assert.False(available);
        Assert.Null(runner);
        Assert.Null(resolvedPath);
    }

    [Fact]
    public void TryFindAvailableRunnerVersion_ReturnsDefinitionAndPathForRemovedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var runnerPath = temp.GetPath("GE-Proton-removed");
        var settings = new AppSettings
        {
            RunnerVersions =
            [
                new RunnerVersionConfig
                {
                    Id = "selected-runner",
                    Name = "GE-Proton removed",
                    Kind = RunnerVersionKind.Proton,
                    Path = runnerPath
                }
            ]
        };

        var available = RunnerVersionEnvironmentHelper.TryFindAvailableRunnerVersion(
            settings,
            "selected-runner",
            out var runner,
            out var resolvedPath);

        Assert.False(available);
        Assert.NotNull(runner);
        Assert.Equal(runnerPath, resolvedPath);
    }

    [Fact]
    public void TryFindAvailableRunnerVersion_AcceptsCompleteSelectedRunner()
    {
        using var temp = new TemporaryDirectory();
        var runnerPath = temp.CreateDirectory("GE-Proton");
        temp.CreateFile(Path.Combine("GE-Proton", "proton"));
        temp.CreateFile(Path.Combine("GE-Proton", "toolmanifest.vdf"));
        var settings = new AppSettings
        {
            RunnerVersions =
            [
                new RunnerVersionConfig
                {
                    Id = "selected-runner",
                    Name = "GE-Proton",
                    Kind = RunnerVersionKind.Proton,
                    Path = runnerPath
                }
            ]
        };

        var available = RunnerVersionEnvironmentHelper.TryFindAvailableRunnerVersion(
            settings,
            "selected-runner",
            out var runner,
            out var resolvedPath);

        Assert.True(available);
        Assert.NotNull(runner);
        Assert.Equal(runnerPath, resolvedPath);
    }
}
