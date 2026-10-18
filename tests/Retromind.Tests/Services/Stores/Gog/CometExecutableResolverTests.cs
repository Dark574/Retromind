using Retromind.Services.Stores.Gog.Comet;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class CometExecutableResolverTests
{
    [Fact]
    public void Resolve_PrefersBundledAppImageExecutable()
    {
        using var temp = new TemporaryDirectory();
        var appBase = Directory.CreateDirectory(Path.Combine(temp.RootPath, "app-base"));
        var appDir = Directory.CreateDirectory(Path.Combine(temp.RootPath, "app-dir"));
        var appBaseExecutable = CreateFile(Path.Combine(appBase.FullName, CometExecutableResolver.ExecutableName));
        var bundledExecutable = CreateFile(
            Path.Combine(appDir.FullName, "usr", "bin", CometExecutableResolver.ExecutableName));
        var resolver = new CometExecutableResolver(appBase.FullName, appDir.FullName);

        var result = resolver.Resolve();

        Assert.Equal(Path.GetFullPath(bundledExecutable), result);
        Assert.NotEqual(Path.GetFullPath(appBaseExecutable), result);
    }

    [Fact]
    public void Resolve_UsesExecutableBesidePublishedApplication()
    {
        using var temp = new TemporaryDirectory();
        var executable = CreateFile(Path.Combine(temp.RootPath, CometExecutableResolver.ExecutableName));
        var resolver = new CometExecutableResolver(temp.RootPath, appDir: null);

        Assert.Equal(Path.GetFullPath(executable), resolver.Resolve());
    }

    [Fact]
    public void Resolve_DoesNotSearchPath()
    {
        using var temp = new TemporaryDirectory();
        var pathDirectory = Directory.CreateDirectory(Path.Combine(temp.RootPath, "path"));
        CreateFile(Path.Combine(pathDirectory.FullName, CometExecutableResolver.ExecutableName));
        var resolver = new CometExecutableResolver(
            Path.Combine(temp.RootPath, "app-base"),
            appDir: null);

        Assert.Null(resolver.Resolve());
    }

    [Fact]
    public void GetRequiredExecutable_ReportsMissingBundledTool()
    {
        using var temp = new TemporaryDirectory();
        var resolver = new CometExecutableResolver(temp.RootPath, appDir: null);

        var exception = Assert.Throws<CometProcessException>(resolver.GetRequiredExecutable);

        Assert.Contains("not available", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
