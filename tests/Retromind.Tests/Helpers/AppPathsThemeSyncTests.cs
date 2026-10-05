using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class AppPathsThemeSyncTests
{
    [Fact]
    public void EnsurePortableThemeDirectory_UpdatesThroughValidatedSwap()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var shippedDir = CreateShippedTheme(temp, "old theme", includeLegacyFile: true);
        var targetDir = Path.Combine(AppPaths.ThemesRoot, "Default");
        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        File.WriteAllText(Path.Combine(shippedDir, "theme.axaml"), "new shipped theme content");
        File.Delete(Path.Combine(shippedDir, "legacy.txt"));
        File.WriteAllText(Path.Combine(shippedDir, "new.txt"), "new asset");

        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        Assert.Equal("new shipped theme content", File.ReadAllText(Path.Combine(targetDir, "theme.axaml")));
        Assert.False(File.Exists(Path.Combine(targetDir, "legacy.txt")));
        Assert.True(File.Exists(Path.Combine(targetDir, "new.txt")));
        var paths = AppPaths.GetThemeUpdatePaths(targetDir);
        Assert.False(Directory.Exists(paths.TransactionDirectory));
    }

    [Fact]
    public void EnsurePortableThemeDirectory_WhenStagingCannotBeCreated_KeepsInstalledTheme()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var shippedDir = CreateShippedTheme(temp, "old theme");
        var targetDir = Path.Combine(AppPaths.ThemesRoot, "Default");
        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);
        File.WriteAllText(Path.Combine(shippedDir, "theme.axaml"), "new shipped theme content");

        var paths = AppPaths.GetThemeUpdatePaths(targetDir);
        Directory.CreateDirectory(paths.TransactionDirectory);
        File.WriteAllText(paths.StagingDirectory, "blocks staging directory creation");

        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        Assert.Equal("old theme", File.ReadAllText(Path.Combine(targetDir, "theme.axaml")));
    }

    [Fact]
    public void EnsurePortableThemeDirectory_WhenFirstInstallStagingFails_DoesNotPublishPartialTheme()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var shippedDir = CreateShippedTheme(temp, "shipped theme");
        var targetDir = Path.Combine(AppPaths.ThemesRoot, "Default");
        var paths = AppPaths.GetThemeUpdatePaths(targetDir);
        Directory.CreateDirectory(paths.TransactionDirectory);
        File.WriteAllText(paths.StagingDirectory, "blocks staging directory creation");

        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        Assert.False(Directory.Exists(targetDir));
    }

    [Fact]
    public void EnsurePortableThemeDirectory_RecoversInterruptedSwapBeforeSyncing()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var shippedDir = CreateShippedTheme(temp, "installed theme");
        var targetDir = Path.Combine(AppPaths.ThemesRoot, "Default");
        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        var paths = AppPaths.GetThemeUpdatePaths(targetDir);
        Directory.CreateDirectory(paths.TransactionDirectory);
        Directory.Move(targetDir, paths.BackupDirectory);
        Directory.CreateDirectory(paths.StagingDirectory);
        File.WriteAllText(Path.Combine(paths.StagingDirectory, "theme.axaml"), "partial update");

        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        Assert.Equal("installed theme", File.ReadAllText(Path.Combine(targetDir, "theme.axaml")));
        Assert.False(Directory.Exists(paths.TransactionDirectory));
    }

    [Fact]
    public void EnsurePortableThemeDirectory_PreservesUserModifiedTheme()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var shippedDir = CreateShippedTheme(temp, "old theme");
        var targetDir = Path.Combine(AppPaths.ThemesRoot, "Default");
        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        File.WriteAllText(Path.Combine(targetDir, "theme.axaml"), "user customization");
        File.WriteAllText(Path.Combine(shippedDir, "theme.axaml"), "new shipped theme content");

        AppPaths.EnsurePortableThemeDirectory(shippedDir, targetDir);

        Assert.Equal("user customization", File.ReadAllText(Path.Combine(targetDir, "theme.axaml")));
    }

    private static string CreateShippedTheme(
        TemporaryDirectory temp,
        string themeContent,
        bool includeLegacyFile = false)
    {
        var shippedDir = temp.CreateDirectory("Shipped", "Default");
        File.WriteAllText(Path.Combine(shippedDir, "theme.axaml"), themeContent);
        if (includeLegacyFile)
            File.WriteAllText(Path.Combine(shippedDir, "legacy.txt"), "legacy asset");

        return shippedDir;
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));
}
