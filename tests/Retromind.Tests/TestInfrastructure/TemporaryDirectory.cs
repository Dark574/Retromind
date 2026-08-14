namespace Retromind.Tests.TestInfrastructure;

internal sealed class TemporaryDirectory : IDisposable
{
    private const string DirectoryPrefix = "retromind-tests-";

    public TemporaryDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            DirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetPath(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = RootPath;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Combine(parts);
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = GetPath(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string relativePath, string content = "test")
    {
        var path = GetPath(relativePath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
            return;

        EnsureSafeCleanupPath(RootPath);
        DeleteTreeWithoutFollowingLinks(RootPath);
    }

    private static void EnsureSafeCleanupPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var relative = Path.GetRelativePath(tempRoot, fullPath);
        var directoryName = Path.GetFileName(fullPath);

        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !directoryName.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean unsafe test path '{fullPath}'.");
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(string directoryPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isSymbolicLink = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isSymbolicLink)
            {
                if (isDirectory)
                    Directory.Delete(entry, recursive: false);
                else
                    File.Delete(entry);
                continue;
            }

            if (isDirectory)
                DeleteTreeWithoutFollowingLinks(entry);
            else
                File.Delete(entry);
        }

        Directory.Delete(directoryPath, recursive: false);
    }
}
