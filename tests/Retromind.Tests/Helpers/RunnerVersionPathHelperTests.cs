using Retromind.Helpers;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class RunnerVersionPathHelperTests
{
    [Fact]
    public void ResolveExecutablePath_AcceptsCompleteProtonDirectory()
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory("GE-Proton");
        var protonExecutable = temp.CreateFile(Path.Combine("GE-Proton", "proton"));
        temp.CreateFile(Path.Combine("GE-Proton", "toolmanifest.vdf"));

        var resolved = RunnerVersionPathHelper.ResolveExecutablePath(
            RunnerVersionKind.Proton,
            runnerDirectory);

        Assert.Equal(protonExecutable, resolved);
    }

    [Fact]
    public void ResolveExecutablePath_RejectsRemovedProtonDirectory()
    {
        using var temp = new TemporaryDirectory();
        var removedRunnerDirectory = temp.GetPath("GE-Proton-removed");

        var resolved = RunnerVersionPathHelper.ResolveExecutablePath(
            RunnerVersionKind.Proton,
            removedRunnerDirectory);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("proton")]
    [InlineData("toolmanifest.vdf")]
    public void ResolveExecutablePath_RejectsIncompleteProtonDirectory(string onlyFile)
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory("GE-Proton-incomplete");
        temp.CreateFile(Path.Combine("GE-Proton-incomplete", onlyFile));

        var resolved = RunnerVersionPathHelper.ResolveExecutablePath(
            RunnerVersionKind.Proton,
            runnerDirectory);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveExecutablePath_AcceptsWineDirectory()
    {
        using var temp = new TemporaryDirectory();
        var runnerDirectory = temp.CreateDirectory("Wine");
        var wineExecutable = temp.CreateFile(Path.Combine("Wine", "bin", "wine"));

        var resolved = RunnerVersionPathHelper.ResolveExecutablePath(
            RunnerVersionKind.Wine,
            runnerDirectory);

        Assert.Equal(wineExecutable, resolved);
    }

    [Fact]
    public void ResolveConfiguredPath_RejectsRelativePathOutsideDataRoot()
    {
        var resolved = RunnerVersionPathHelper.ResolveConfiguredPath("../GE-Proton");

        Assert.Null(resolved);
    }
}
