using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class LaunchExecutablePathHelperTests
{
    [Fact]
    public void ResolveForDisplay_ResolvesCommandTokenFromFirstPathMatch()
    {
        using var temp = new TemporaryDirectory();
        var firstDirectory = temp.CreateDirectory("first-bin");
        var secondDirectory = temp.CreateDirectory("second-bin");
        var firstWine = temp.CreateFile(Path.Combine("first-bin", "wine"));
        temp.CreateFile(Path.Combine("second-bin", "wine"));
        var pathValue = string.Join(Path.PathSeparator, firstDirectory, secondDirectory);

        var result = LaunchExecutablePathHelper.ResolveForDisplay("wine", pathValue);

        Assert.True(result.IsAvailable);
        Assert.True(result.UsesPathLookup);
        Assert.Equal("wine", result.LaunchValue);
        Assert.Equal(firstWine, result.ResolvedPath);
    }

    [Fact]
    public void ResolveForDisplay_ReportsMissingCommandToken()
    {
        using var temp = new TemporaryDirectory();
        var pathValue = temp.CreateDirectory("empty-bin");

        var result = LaunchExecutablePathHelper.ResolveForDisplay("wine", pathValue);

        Assert.False(result.IsAvailable);
        Assert.True(result.UsesPathLookup);
        Assert.Null(result.ResolvedPath);
    }

    [Fact]
    public void ResolveForDisplay_ResolvesPortableRelativeExecutable()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var relativePath = Path.Combine("Emulators", "wine", "bin", "wine");
        var executablePath = temp.CreateFile(relativePath);

        var result = LaunchExecutablePathHelper.ResolveForDisplay(relativePath);

        Assert.True(result.IsAvailable);
        Assert.False(result.UsesPathLookup);
        Assert.Equal(executablePath, result.ResolvedPath);
    }

    [Fact]
    public void ResolveConfiguredPath_PreservesBackslashRelativePathBehaviorOnLinux()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));

        var resolved = LaunchExecutablePathHelper.ResolveConfiguredPath("Emulators\\wine");

        Assert.Equal(temp.GetPath("Emulators\\wine"), resolved);
    }

    [Theory]
    [InlineData("/opt/wine/bin/wine", true)]
    [InlineData("/opt/wine/bin/wine64", true)]
    [InlineData("/usr/bin/retroarch", false)]
    public void IsWineExecutable_RecognizesWineEntryPoints(string path, bool expected)
    {
        Assert.Equal(expected, LaunchExecutablePathHelper.IsWineExecutable(path));
    }
}
