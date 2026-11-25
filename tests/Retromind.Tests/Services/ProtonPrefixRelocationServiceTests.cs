using System.Diagnostics;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class ProtonPrefixRelocationServiceTests
{
    [Theory]
    [InlineData("v2", true, false)]
    [InlineData("v2", false, true)]
    [InlineData("v2", true, true)]
    [InlineData("root", true, true)]
    [InlineData("alias", true, true)]
    [InlineData("nested", true, true)]
    public void Repair_AfterMovesRepairsOnlyTrackedRuntimeLinksInTheResolvedTree(
        string layout, bool moveLibrary, bool moveRunner)
    {
        using var temp = new TemporaryDirectory();
        var library = temp.CreateDirectory("Library");
        var runner = temp.CreateDirectory("Proton");
        var relative = Path.Combine("Prefixes", "Move Game");
        var item = new MediaItem("Move Game") { PrefixPath = relative };
        var root = Path.Combine(library, relative);
        var runtimeFile = Path.Combine("files", "lib", "wine", "x86_64-windows", "kernel32.dll");
        var target = temp.CreateFile(Path.Combine("Proton", runtimeFile), "builtin");
        var nested = layout is "v2" or "nested";
        var tree = nested ? Path.Combine(root, "pfx") : root;
        if (layout == "v2")
            new WinePrefixService(library, new AppSettings()).Prepare(item, new ProcessStartInfo(), WinePrefixRuntime.Wine);
        else if (layout == "alias")
            Directory.CreateSymbolicLink(Path.Combine(Directory.CreateDirectory(root).FullName, "pfx"), ".");

        var system = Directory.CreateDirectory(Path.Combine(tree, "drive_c", "windows", "system32")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(tree, "drive_c", "users", "steamuser", "Documents")).FullName;
        var save = Path.Combine(profile, "save.dat");
        File.WriteAllText(save, "keep");
        var drives = Path.Combine(tree, "dosdevices");
        PrefixPathHelper.EnsurePortableGamesDriveMapping(root, drives, library);
        var r = new FileInfo(Path.Combine(drives, "r:")).LinkTarget;
        Assert.NotNull(r);
        Assert.False(Path.IsPathRooted(r));
        var runtimeLink = Path.Combine(system, "kernel32.dll");
        File.CreateSymbolicLink(runtimeLink, target);
        var customTarget = temp.GetPath("Absent Data", runtimeFile);
        File.CreateSymbolicLink(Path.Combine(system, "custom.dll"), customTarget);
        File.CreateSymbolicLink(Path.Combine(profile, "custom.dll"), customTarget);
        File.WriteAllText(Path.Combine(root, "version"), "11.0-100");
        File.WriteAllText(Path.Combine(root, "tracked_files"),
            "drive_c/windows/system32/kernel32.dll\ndrive_c/users/steamuser/Documents/custom.dll\n");

        if (moveLibrary)
        {
            var destination = temp.GetPath("Moved Library");
            Directory.Move(library, destination);
            library = destination;
        }
        if (moveRunner)
        {
            var destination = temp.GetPath("Moved Proton");
            Directory.Move(runner, destination);
            runner = destination;
        }

        root = Path.Combine(library, relative);
        tree = nested ? Path.Combine(root, "pfx") : root;
        var plan = WinePrefixService.ResolveManaged(root, WinePrefixRuntime.Wine, library);
        var repair = new ProtonPrefixRelocationService(library, new AppSettings()).Repair(item, plan, runner);
        Assert.Equal(tree, repair.PrefixPath);
        Assert.True(repair.IsProtonManagedPrefix);
        Assert.Equal(moveRunner ? 1 : 0, repair.RepairedLinks);
        Assert.Equal(0, repair.FailedLinks);
        Assert.Equal(Path.Combine(runner, runtimeFile),
            new FileInfo(Path.Combine(tree, "drive_c", "windows", "system32", "kernel32.dll")).LinkTarget);
        Assert.Equal(customTarget,
            new FileInfo(Path.Combine(tree, "drive_c", "windows", "system32", "custom.dll")).LinkTarget);
        Assert.Equal(customTarget,
            new FileInfo(Path.Combine(tree, "drive_c", "users", "steamuser", "Documents", "custom.dll")).LinkTarget);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(tree, "drive_c", "users", "steamuser", "Documents", "save.dat")));
        drives = Path.Combine(tree, "dosdevices");
        Assert.Equal(r, new FileInfo(Path.Combine(drives, "r:")).LinkTarget);
        Assert.Equal(Path.Combine(library, "Games"), Path.GetFullPath(Path.Combine(drives, r)));
    }

    [Fact]
    public void Repair_RebasesBrokenProtonRuntimeLinksToConfiguredRunner()
    {
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
    public void Repair_RebasesReachableTrackedProtonLinkToConfiguredRunner()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Copied Game");
        var oldRunnerRoot = CreateProtonRunner(temp, "Original Proton");
        var currentRunnerRoot = CreateProtonRunner(temp, "Portable Proton");
        var relativeRuntimePath = Path.Combine("lib", "wine", "x86_64-windows", "kernel32.dll");
        var oldTarget = temp.CreateFile(
            Path.Combine("Original Proton", "files", relativeRuntimePath),
            "original-runtime");
        var replacementTarget = temp.CreateFile(
            Path.Combine("Portable Proton", "files", relativeRuntimePath),
            "portable-runtime");
        var link = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, oldTarget);
        var settings = CreateSettingsWithProtonRunner("runner-id", currentRunnerRoot);
        var item = new MediaItem("Copied Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Copied Game"),
            RunnerVersionId = "runner-id"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, settings).Repair(item);

        Assert.True(Directory.Exists(oldRunnerRoot));
        Assert.Equal(1, result.RepairedLinks);
        Assert.Equal(0, result.UnresolvedLinks);
        Assert.Equal(replacementTarget, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_RebasesReachableTrackedLinkInNestedProtonPrefix()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var compatRoot = temp.CreateDirectory("Library", "Prefixes", "Nested Game");
        var prefixPath = temp.CreateDirectory("Library", "Prefixes", "Nested Game", "pfx");
        temp.CreateDirectory("Library", "Prefixes", "Nested Game", "pfx", "drive_c");
        temp.CreateFile(
            "Library/Prefixes/Nested Game/tracked_files",
            "drive_c/windows/system32/kernel32.dll\n");
        temp.CreateFile("Library/Prefixes/Nested Game/version", "11.0-100\n");
        CreateProtonRunner(temp, "Original Proton");
        var currentRunnerRoot = CreateProtonRunner(temp, "Portable Proton");
        var relativeRuntimePath = Path.Combine("lib", "wine", "x86_64-windows", "kernel32.dll");
        var oldTarget = temp.CreateFile(
            Path.Combine("Original Proton", "files", relativeRuntimePath),
            "original-runtime");
        var replacementTarget = temp.CreateFile(
            Path.Combine("Portable Proton", "files", relativeRuntimePath),
            "portable-runtime");
        var link = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, oldTarget);
        var settings = CreateSettingsWithProtonRunner("runner-id", currentRunnerRoot);
        var item = new MediaItem("Nested Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Nested Game"),
            RunnerVersionId = "runner-id"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, settings).Repair(item);

        Assert.True(result.IsProtonManagedPrefix);
        Assert.Equal(prefixPath, result.PrefixPath);
        Assert.Equal(compatRoot, Path.GetDirectoryName(result.PrefixPath));
        Assert.Equal(1, result.RepairedLinks);
        Assert.Equal(replacementTarget, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_LeavesValidProtonLinkUnchanged()
    {
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
    public void Repair_LeavesReachableUntrackedLinkUnchanged()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Custom Link Game");
        CreateProtonRunner(temp, "Original Proton");
        var currentRunnerRoot = CreateProtonRunner(temp, "Portable Proton");
        var relativeRuntimePath = Path.Combine("lib", "wine", "x86_64-windows", "custom.dll");
        var oldTarget = temp.CreateFile(
            Path.Combine("Original Proton", "files", relativeRuntimePath),
            "custom-original");
        temp.CreateFile(
            Path.Combine("Portable Proton", "files", relativeRuntimePath),
            "custom-portable");
        var link = Path.Combine(prefixPath, "drive_c", "windows", "system32", "custom.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, oldTarget);
        var settings = CreateSettingsWithProtonRunner("runner-id", currentRunnerRoot);
        var item = new MediaItem("Custom Link Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Custom Link Game"),
            RunnerVersionId = "runner-id"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, settings).Repair(item);

        Assert.Equal(0, result.RepairedLinks);
        Assert.Equal(0, result.UnresolvedLinks);
        Assert.Equal(oldTarget, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_PreservesReachableTrackedLinkWhenConfiguredRunnerHasNoEquivalent()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var prefixPath = CreateProtonPrefix(temp, "Library", "Prefixes", "Missing Replacement Game");
        CreateProtonRunner(temp, "Original Proton");
        var currentRunnerRoot = CreateProtonRunner(temp, "Portable Proton");
        var relativeRuntimePath = Path.Combine("lib", "wine", "x86_64-windows", "kernel32.dll");
        var oldTarget = temp.CreateFile(
            Path.Combine("Original Proton", "files", relativeRuntimePath),
            "original-runtime");
        var link = Path.Combine(prefixPath, "drive_c", "windows", "system32", "kernel32.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, oldTarget);
        var settings = CreateSettingsWithProtonRunner("runner-id", currentRunnerRoot);
        var item = new MediaItem("Missing Replacement Game")
        {
            PrefixPath = Path.Combine("Prefixes", "Missing Replacement Game"),
            RunnerVersionId = "runner-id"
        };

        var result = new ProtonPrefixRelocationService(libraryRoot, settings).Repair(item);

        Assert.Equal(0, result.RepairedLinks);
        Assert.Equal(1, result.UnresolvedLinks);
        Assert.Equal(oldTarget, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Repair_ReportsBrokenLinksWhenConfiguredRunnerIsUnavailable()
    {
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
