using System.Diagnostics;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogWindowsShortcutPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplyInstallerEnvironment_DisablesWineMenuBuilderWhenBothOptionsAreOff()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["WINEDLLOVERRIDES"] = "winhttp=n,b";

        GogWindowsShortcutPolicy.ApplyInstallerEnvironment(startInfo, false, false);

        Assert.Equal(
            "winhttp=n,b;winemenubuilder.exe=d",
            startInfo.Environment["WINEDLLOVERRIDES"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApplyInstallerEnvironment_KeepsWineMenuBuilderForRequestedIntegration(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        var startInfo = new ProcessStartInfo();

        GogWindowsShortcutPolicy.ApplyInstallerEnvironment(
            startInfo,
            createDesktopShortcut,
            createStartMenuShortcuts);

        Assert.False(startInfo.Environment.ContainsKey("WINEDLLOVERRIDES"));
    }

    [Fact]
    public void RemoveUnwantedExports_RemovesOnlyDisabledCategoryAndExactPrefix()
    {
        var prefix = Path.Combine(_root, "prefix");
        var home = Path.Combine(_root, "home");
        var desktopDirectory = Path.Combine(prefix, "drive_c", "users", "tester", "Desktop");
        var menuDirectory = Path.Combine(home, ".local", "share", "applications", "wine");
        Directory.CreateDirectory(desktopDirectory);
        Directory.CreateDirectory(menuDirectory);

        var desktopEntry = WriteDesktopFile(desktopDirectory, "Desktop Game.desktop", prefix);
        var menuEntry = WriteDesktopFile(menuDirectory, "Menu Game.desktop", prefix);
        var unrelatedEntry = WriteDesktopFile(
            menuDirectory,
            "Other Game.desktop",
            Path.Combine(_root, "other-prefix"));
        var environment = new Dictionary<string, string?> { ["HOME"] = home };

        var removed = GogWindowsShortcutPolicy.RemoveUnwantedExports(
            prefix,
            createDesktopShortcut: false,
            createStartMenuShortcuts: true,
            environment);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(desktopEntry));
        Assert.True(File.Exists(menuEntry));
        Assert.True(File.Exists(unrelatedEntry));

        removed = GogWindowsShortcutPolicy.RemoveUnwantedExports(
            prefix,
            createDesktopShortcut: true,
            createStartMenuShortcuts: false,
            environment);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(menuEntry));
        Assert.True(File.Exists(unrelatedEntry));
    }

    private static string WriteDesktopFile(string directory, string name, string prefix)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"Exec=env \"WINEPREFIX={prefix}\" wine game.exe\n");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
