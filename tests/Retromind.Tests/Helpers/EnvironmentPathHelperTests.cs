using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class EnvironmentPathHelperTests
{
    [Fact]
    public void TryGetUserHomePathFromPasswd_ReturnsMatchingNonEmptyHome()
    {
        using var temp = new TemporaryDirectory();
        var passwdPath = temp.CreateFile(
            "passwd",
            "other:x:1000:1000::/home/other:/bin/bash\n" +
            "neville:x:1001:1001::/home/neville:/bin/bash\n");

        var homePath = EnvironmentPathHelper.TryGetUserHomePathFromPasswd("neville", passwdPath);

        Assert.Equal("/home/neville", homePath);
        Assert.Null(EnvironmentPathHelper.TryGetUserHomePathFromPasswd("missing", passwdPath));
    }

    [Fact]
    public void TryFindExecutableInPath_ReturnsFirstExistingCandidate()
    {
        using var temp = new TemporaryDirectory();
        var missingDirectory = temp.CreateDirectory("missing");
        var binDirectory = temp.CreateDirectory("bin");
        var executablePath = temp.CreateFile(Path.Combine("bin", "umu-run"));
        var pathValue = string.Join(Path.PathSeparator, missingDirectory, binDirectory);

        var resolved = EnvironmentPathHelper.TryFindExecutableInPath("umu-run", pathValue);

        Assert.Equal(executablePath, resolved);
    }

    [Fact]
    public void ResolveExecutablePathForExistenceCheck_DistinguishesPathFromCommandToken()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", Path.Combine(temp.RootPath, "Retromind.AppImage")),
            ("APPDIR", null));
        var relativePath = Path.Combine("Library", "Games", "game.sh");

        Assert.Null(EnvironmentPathHelper.ResolveExecutablePathForExistenceCheck("umu-run"));
        Assert.Equal(
            Path.Combine(temp.RootPath, relativePath),
            EnvironmentPathHelper.ResolveExecutablePathForExistenceCheck(relativePath));
    }
}
