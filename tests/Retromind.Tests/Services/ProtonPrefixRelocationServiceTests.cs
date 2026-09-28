using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ProtonPrefixRelocationServiceTests
{
    [Fact]
    public void Repair_RebasesOnlyBrokenProtonRuntimeLinksToConfiguredRunner()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Portable Game");
        var runnerRoot = CreateProtonRunner(temp, "Current Proton");
        var relativeRuntimePath = Path.Combine("lib", "wine", "x86_64-windows", "kernel32.dll");
        var replacementTarget = temp.CreateFile(
            Path.Combine("Current Proton", "files", relativeRuntimePath),
            "current-runtime");
        var oldTarget = temp.GetPath("Moved Retromind", "Proton", "files", relativeRuntimePath);
        var managedLink = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(managedLink)!);
        File.CreateSymbolicLink(managedLink, oldTarget);

        var unrelatedTarget = temp.GetPath("missing", "custom.dll");
        var unrelatedLink = Path.Combine(prefixPath, "drive_c", "windows", "system32", "custom.dll");
        File.CreateSymbolicLink(unrelatedLink, unrelatedTarget);

        var settings = CreateSettingsWithProtonRunner("runner-id", runnerRoot);
        var item = new MediaItem("Portable Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Portable Game")
        };
        item.CustomFields[CustomFieldKeyHelper.StoreInstallRunnerVersionId] = "runner-id";
        var service = new ProtonPrefixRelocationService(libraryRoot, settings);

        var result = service.Repair(item);

        Assert.True(result.IsProtonManagedPrefix);
        Assert.Equal(1, result.RepairedLinks);
        Assert.Equal(0, result.UnresolvedLinks);
        Assert.Equal(0, result.FailedLinks);
        Assert.Equal(replacementTarget, new FileInfo(managedLink).LinkTarget);
        Assert.Equal(unrelatedTarget, new FileInfo(unrelatedLink).LinkTarget);
    }

    [Fact]
    public void Repair_LeavesValidProtonLinkUnchanged()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Working Game");
        var runnerRoot = CreateProtonRunner(temp, "Working Proton");
        var target = temp.CreateFile(
            "Working Proton/files/lib/wine/x86_64-windows/kernel32.dll",
            "runtime");
        var link = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        var settings = CreateSettingsWithProtonRunner("runner-id", runnerRoot);
        var item = new MediaItem("Working Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Working Game"),
            RunnerVersionId = "runner-id"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, settings).Repair(item);

        Assert.True(result.IsProtonManagedPrefix);
        Assert.Equal(0, result.RepairedLinks);
        Assert.Equal(target, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_ReportsBrokenLinksWhenConfiguredRunnerIsUnavailable()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Missing Runner Game");
        var brokenLink = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenLink)!);
        File.CreateSymbolicLink(
            brokenLink,
            temp.GetPath("Old Proton", "files", "lib", "wine", "x86_64-windows", "kernel32.dll"));
        var item = new MediaItem("Missing Runner Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Missing Runner Game"),
            RunnerVersionId = "missing-runner"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, new AppSettings()).Repair(item);

        Assert.True(result.IsProtonManagedPrefix);
        Assert.Equal(0, result.RepairedLinks);
        Assert.Equal(1, result.UnresolvedLinks);
        Assert.Null(result.ProtonRootPath);
    }

    private static string CreateProtonPrefix(
        TemporaryDirectory temp,
        params string[] pathParts)
    {
        var prefixPath = temp.CreateDirectory(pathParts);
        temp.CreateDirectory(Path.Combine([.. pathParts, "drive_c"]));
        temp.CreateFile(Path.Combine([.. pathParts, "tracked_files"]), "drive_c/windows/system32/kernel32.dll\n");
        temp.CreateFile(Path.Combine([.. pathParts, "version"]), "11.0-100\n");
        return prefixPath;
    }

    private static string CreateProtonRunner(TemporaryDirectory temp, string folderName)
    {
        var root = temp.CreateDirectory(folderName);
        temp.CreateDirectory(folderName, "files");
        return root;
    }

    private static AppSettings CreateSettingsWithProtonRunner(string id, string runnerRoot) =>
        new()
        {
            RunnerVersions =
            [
                new RunnerVersionConfig
                {
                    Id = id,
                    Name = "Test Proton",
                    Kind = RunnerVersionKind.Proton,
                    Path = runnerRoot
                }
            ]
        };
}
