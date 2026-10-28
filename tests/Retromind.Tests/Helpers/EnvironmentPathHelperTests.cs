using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class EnvironmentPathHelperTests
{
    [Theory]
    [InlineData("PROTONPATH")]
    [InlineData("STEAM_COMPAT_DATA_PATH")]
    [InlineData("HOME")]
    [InlineData("DOTNET_CLI_HOME")]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("XDG_DATA_HOME")]
    [InlineData("XDG_CACHE_HOME")]
    [InlineData("XDG_STATE_HOME")]
    public void ConvertDataRootPathsToPortable_ConvertsKnownInternalPath(string key)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var overrides = new Dictionary<string, string>
        {
            [key] = temp.GetPath("Emulators", "Portable")
        };

        var converted = EnvironmentPathHelper.ConvertDataRootPathsToPortable(overrides);

        Assert.Equal(1, converted);
        Assert.Equal(Path.Combine("Emulators", "Portable"), overrides[key]);
    }

    [Fact]
    public void ConvertDataRootPathsToPortable_LeavesExternalAndUnknownValuesUntouched()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", portableRoot.GetPath("Retromind.AppImage")),
            ("APPDIR", null));
        var externalHome = externalRoot.GetPath("home");
        var customInternalValue = portableRoot.GetPath("custom", "value");
        var overrides = new Dictionary<string, string>
        {
            ["HOME"] = externalHome,
            ["RETROMIND_CUSTOM"] = customInternalValue
        };

        var converted = EnvironmentPathHelper.ConvertDataRootPathsToPortable(overrides);

        Assert.Equal(0, converted);
        Assert.Equal(externalHome, overrides["HOME"]);
        Assert.Equal(customInternalValue, overrides["RETROMIND_CUSTOM"]);
    }

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
