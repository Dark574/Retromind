using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Tests.Services.Stores.Epic;

public sealed class LegendaryExecutableResolverTests
{
    [Fact]
    public void Resolve_PrefersRetromindManagedExecutable()
    {
        using var temp = new TempDirectory();
        var managedPath = LegendaryExecutableResolver.GetManagedExecutablePath(temp.RootPath);
        var pathDirectory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "path"));
        var pathExecutable = Path.Combine(pathDirectory.FullName, LegendaryExecutableResolver.ExecutableName);
        CreateFile(managedPath);
        CreateFile(pathExecutable);

        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            pathDirectory.FullName);

        Assert.Equal(Path.GetFullPath(managedPath), resolver.Resolve());
    }

    [Fact]
    public void Resolve_FallsBackToAbsolutePathEntry()
    {
        using var temp = new TempDirectory();
        var pathDirectory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "path"));
        var pathExecutable = Path.Combine(pathDirectory.FullName, LegendaryExecutableResolver.ExecutableName);
        CreateFile(pathExecutable);

        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            $"relative{Path.PathSeparator}{pathDirectory.FullName}");

        Assert.Equal(Path.GetFullPath(pathExecutable), resolver.Resolve());
    }

    [Fact]
    public void GetRequiredExecutable_ReportsMissingManagedTool()
    {
        using var temp = new TempDirectory();
        var resolver = new LegendaryExecutableResolver(
            temp.RootPath,
            Path.Combine(temp.RootPath, "app"),
            appDir: null,
            pathEnvironment: null);

        var exception = Assert.Throws<LegendaryCliException>(resolver.GetRequiredExecutable);

        Assert.Contains("not available", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "retromind-legendary-resolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        internal string RootPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
