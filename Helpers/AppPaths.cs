using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Retromind.Helpers;

public static class AppPaths
{
    private const string ThemeManifestFileName = ".retromind-theme.json";

    private sealed class ThemeManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string? SourceHash { get; set; }
        public string? InstalledHash { get; set; }
        public DateTime InstalledUtc { get; set; }
    }

    /// <summary>
    /// Writable "portable" root for app data (Library, JSON files, etc.).
    /// - AppImage: directory containing the AppImage file (ENV: APPIMAGE)
    /// - Fallback: AppContext.BaseDirectory (normal publish/run)
    /// </summary>
    public static string DataRoot
    {
        get
        {
            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImagePath))
            {
                var dir = Path.GetDirectoryName(appImagePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }

            return AppContext.BaseDirectory;
        }
    }

    public static string LibraryRoot => Path.Combine(DataRoot, "Library");
    
    // Themes live in the portable data root so users can edit them next to the AppImage.
    public static string ThemesRoot => Path.Combine(DataRoot, "Themes");
    
    // --- path helpers for portable storage ---

    /// <summary>
    /// Ensures Themes exist in DataRoot (portable).
    /// Copies missing shipped Themes from AppContext.BaseDirectory.
    /// Also restores missing theme directories (directories containing theme.axaml)
    /// from shipped content.
    /// Updates shipped themes only if they remain unmodified locally.
    /// </summary>
    public static void EnsurePortableThemes()
    {
        try
        {
            if (!Directory.Exists(ThemesRoot))
                Directory.CreateDirectory(ThemesRoot);

            // Copy defaults shipped with the app (build output).
            var shippedThemesRoot = Path.Combine(AppContext.BaseDirectory, "Themes");
            if (!Directory.Exists(shippedThemesRoot))
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(shippedThemesRoot))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var target = Path.Combine(ThemesRoot, name);

                if (Directory.Exists(entry))
                {
                    EnsurePortableThemeDirectory(entry, target);
                }
                else if (File.Exists(entry))
                {
                    if (File.Exists(target))
                        continue;

                    File.Copy(entry, target, overwrite: false);
                }
            }

            EnsureShippedThemeDirectories(shippedThemesRoot);
        }
        catch
        {
            // best-effort; themes must never break startup
        }
    }

    internal static void EnsurePortableThemeDirectory(string shippedDir, string targetDir)
    {
        try
        {
            RecoverInterruptedThemeUpdate(targetDir);

            if (!Directory.Exists(targetDir))
            {
                PublishPortableThemeDirectory(shippedDir, targetDir);
                return;
            }

            var manifest = TryReadThemeManifest(targetDir);
            if (!IsThemeManifestUsable(manifest))
            {
                if (!TryRecoverThemeManifest(targetDir, shippedDir))
                    return;

                manifest = TryReadThemeManifest(targetDir);
                if (!IsThemeManifestUsable(manifest))
                    return;
            }
            var verifiedManifest = manifest!;

            var targetHash = ComputeDirectoryHash(targetDir);
            if (!string.Equals(targetHash, verifiedManifest.InstalledHash, StringComparison.OrdinalIgnoreCase))
                return; // User modified the theme locally.

            var shippedHash = ComputeDirectoryHash(shippedDir);
            if (string.Equals(shippedHash, verifiedManifest.SourceHash, StringComparison.OrdinalIgnoreCase))
                return; // No shipped updates.

            PublishPortableThemeDirectory(shippedDir, targetDir);
        }
        catch
        {
            // best-effort; never break startup due to theme sync
        }
    }

    internal static (string TransactionDirectory, string StagingDirectory, string BackupDirectory)
        GetThemeUpdatePaths(string targetDir)
    {
        var normalizedTarget = Path.GetFullPath(targetDir);
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTarget));
        var key = Convert.ToHexString(keyBytes).ToLowerInvariant();
        var transactionDirectory = Path.Combine(ThemesRoot, ".retromind-theme-updates", key);
        return (
            transactionDirectory,
            Path.Combine(transactionDirectory, "staging"),
            Path.Combine(transactionDirectory, "backup"));
    }

    private static void PublishPortableThemeDirectory(string shippedDir, string targetDir)
    {
        var paths = GetThemeUpdatePaths(targetDir);
        Directory.CreateDirectory(paths.TransactionDirectory);

        if (Directory.Exists(paths.StagingDirectory))
            Directory.Delete(paths.StagingDirectory, recursive: true);

        CopyDirectoryRecursive(shippedDir, paths.StagingDirectory);
        WriteThemeManifestOrThrow(paths.StagingDirectory, shippedDir);
        ValidatePreparedThemeOrThrow(paths.StagingDirectory, shippedDir);

        var movedExistingTheme = false;
        try
        {
            if (Directory.Exists(targetDir))
            {
                if (Directory.Exists(paths.BackupDirectory))
                    throw new IOException("A previous theme update backup is still present.");

                Directory.Move(targetDir, paths.BackupDirectory);
                movedExistingTheme = true;
            }

            var targetParent = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrWhiteSpace(targetParent))
                Directory.CreateDirectory(targetParent);

            Directory.Move(paths.StagingDirectory, targetDir);
        }
        catch
        {
            if (movedExistingTheme &&
                !Directory.Exists(targetDir) &&
                Directory.Exists(paths.BackupDirectory))
            {
                Directory.Move(paths.BackupDirectory, targetDir);
            }

            throw;
        }

        if (Directory.Exists(paths.BackupDirectory))
            Directory.Delete(paths.BackupDirectory, recursive: true);

        CleanupThemeUpdateDirectory(paths.TransactionDirectory);
    }

    private static void RecoverInterruptedThemeUpdate(string targetDir)
    {
        var paths = GetThemeUpdatePaths(targetDir);

        if (!Directory.Exists(targetDir) && Directory.Exists(paths.BackupDirectory))
        {
            var targetParent = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrWhiteSpace(targetParent))
                Directory.CreateDirectory(targetParent);

            Directory.Move(paths.BackupDirectory, targetDir);
        }
        else if (Directory.Exists(targetDir) && Directory.Exists(paths.BackupDirectory))
        {
            // The staging directory was already published before the previous process stopped.
            Directory.Delete(paths.BackupDirectory, recursive: true);
        }

        if (Directory.Exists(paths.StagingDirectory))
            Directory.Delete(paths.StagingDirectory, recursive: true);

        CleanupThemeUpdateDirectory(paths.TransactionDirectory);
    }

    private static void CleanupThemeUpdateDirectory(string transactionDirectory)
    {
        if (Directory.Exists(transactionDirectory) &&
            !Directory.EnumerateFileSystemEntries(transactionDirectory).Any())
        {
            Directory.Delete(transactionDirectory);
        }

        var transactionRoot = Path.GetDirectoryName(transactionDirectory);
        if (!string.IsNullOrWhiteSpace(transactionRoot) &&
            Directory.Exists(transactionRoot) &&
            !Directory.EnumerateFileSystemEntries(transactionRoot).Any())
        {
            Directory.Delete(transactionRoot);
        }
    }

    private static void ValidatePreparedThemeOrThrow(string stagingDir, string shippedDir)
    {
        if (!AreDirectoryContentsEquivalent(stagingDir, shippedDir))
            throw new IOException("Prepared theme content does not match the shipped theme.");

        var manifest = TryReadThemeManifest(stagingDir);
        if (!IsThemeManifestUsable(manifest))
            throw new IOException("Prepared theme manifest is missing or invalid.");

        var installedHash = ComputeDirectoryHash(stagingDir);
        if (!string.Equals(installedHash, manifest!.InstalledHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Prepared theme manifest does not match its content.");
    }

    private static bool IsThemeManifestUsable(ThemeManifest? manifest)
    {
        return manifest != null &&
               !string.IsNullOrWhiteSpace(manifest.SourceHash) &&
               !string.IsNullOrWhiteSpace(manifest.InstalledHash);
    }

    private static bool TryRecoverThemeManifest(string targetDir, string shippedDir)
    {
        try
        {
            if (!AreDirectoryContentsEquivalent(targetDir, shippedDir))
                return false;

            WriteThemeManifest(targetDir, shippedDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AreDirectoryContentsEquivalent(string leftDir, string rightDir)
    {
        var leftFiles = Directory.GetFiles(leftDir, "*", SearchOption.AllDirectories)
            .Where(path => !FileSystemPathIdentity.Equals(Path.GetFileName(path), ThemeManifestFileName))
            .Select(path => Path.GetRelativePath(leftDir, path).Replace('\\', '/'))
            .OrderBy(path => path, FileSystemPathIdentity.Comparer)
            .ToArray();

        var rightFiles = Directory.GetFiles(rightDir, "*", SearchOption.AllDirectories)
            .Where(path => !FileSystemPathIdentity.Equals(Path.GetFileName(path), ThemeManifestFileName))
            .Select(path => Path.GetRelativePath(rightDir, path).Replace('\\', '/'))
            .OrderBy(path => path, FileSystemPathIdentity.Comparer)
            .ToArray();

        if (leftFiles.Length != rightFiles.Length)
            return false;

        for (var i = 0; i < leftFiles.Length; i++)
        {
            if (!FileSystemPathIdentity.Equals(leftFiles[i], rightFiles[i]))
                return false;

            var leftPath = Path.Combine(leftDir, leftFiles[i]);
            var rightPath = Path.Combine(rightDir, rightFiles[i]);
            if (!AreFilesEquivalent(leftPath, rightPath))
                return false;
        }

        return true;
    }

    private static bool AreFilesEquivalent(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
            return false;

        const int bufferSize = 81920;
        using var leftStream = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        using var rightStream = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);

        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];

        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);

            if (leftRead != rightRead)
                return false;

            if (leftRead == 0)
                return true;

            for (var i = 0; i < leftRead; i++)
            {
                if (leftBuffer[i] != rightBuffer[i])
                    return false;
            }
        }
    }

    /// <summary>
    /// Restores missing nested theme directories and ensures every shipped directory
    /// containing its own theme.axaml has an independent update manifest. This is
    /// especially important for system subthemes under Themes/System/&lt;Id&gt;.
    /// Existing user-modified themes are left untouched.
    /// </summary>
    private static void EnsureShippedThemeDirectories(string shippedThemesRoot)
    {
        var shippedThemeFiles = Directory.GetFiles(shippedThemesRoot, "theme.axaml", SearchOption.AllDirectories);
        foreach (var shippedThemeFile in shippedThemeFiles)
        {
            var shippedThemeDir = Path.GetDirectoryName(shippedThemeFile);
            if (string.IsNullOrWhiteSpace(shippedThemeDir))
                continue;

            var relativeThemeDir = Path.GetRelativePath(shippedThemesRoot, shippedThemeDir);
            var targetThemeDir = Path.Combine(ThemesRoot, relativeThemeDir);
            EnsurePortableThemeDirectory(shippedThemeDir, targetThemeDir);
        }
    }

    private static ThemeManifest? TryReadThemeManifest(string themeDir)
    {
        var manifestPath = Path.Combine(themeDir, ThemeManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<ThemeManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteThemeManifest(string themeDir, string shippedDir)
    {
        try
        {
            WriteThemeManifestOrThrow(themeDir, shippedDir);
        }
        catch
        {
            // best-effort
        }
    }

    private static void WriteThemeManifestOrThrow(string themeDir, string shippedDir)
    {
        var shippedHash = ComputeDirectoryHash(shippedDir);
        var installedHash = ComputeDirectoryHash(themeDir);

        var manifest = new ThemeManifest
        {
            SourceHash = shippedHash,
            InstalledHash = installedHash,
            InstalledUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var manifestPath = Path.Combine(themeDir, ThemeManifestFileName);
        File.WriteAllText(manifestPath, json);
    }

    private static string ComputeDirectoryHash(string directory)
    {
        using var sha = SHA256.Create();
        var separator = new byte[] { 0 };
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Array.Sort(files, FileSystemPathIdentity.Comparer);

        // Use file metadata to avoid hashing large theme assets at startup.
        foreach (var file in files)
        {
            if (FileSystemPathIdentity.Equals(Path.GetFileName(file), ThemeManifestFileName))
                continue;

            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var info = new FileInfo(file);
            var signature = $"{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var signatureBytes = Encoding.UTF8.GetBytes(signature);
            sha.TransformBlock(signatureBytes, 0, signatureBytes.Length, null, 0);
            sha.TransformBlock(separator, 0, 1, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSub);
        }
    }
    
    /// <summary>
    /// Resolves a stored path to an absolute path under DataRoot.
    /// If the input is already absolute, it is returned unchanged.
    /// </summary>
    public static string ResolveDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DataRoot;

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(DataRoot, path));
    }

    /// <summary>
    /// Resolves a stored path to an absolute path and ensures it stays inside <see cref="DataRoot"/>.
    /// Intended for persisted "relative-to-DataRoot" content paths (assets/manuals/theme files).
    /// Returns false when the path escapes DataRoot (e.g. via "..") or is otherwise invalid.
    /// </summary>
    public static bool TryResolveDataPathInsideRoot(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(DataRoot, path));

            if (!IsPathInsideDataRoot(candidate))
                return false;

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a path below <see cref="DataRoot"/> for a filesystem mutation.
    /// In addition to lexical containment, every existing component below the
    /// trusted data root must be a real filesystem entry rather than a symbolic
    /// link or other reparse point.
    /// </summary>
    public static bool TryResolveDataPathForMutation(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryResolveDataPathInsideRoot(path, out var candidate))
            return false;

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataRoot));
            var relativePath = Path.GetRelativePath(normalizedRoot, candidate);
            if (relativePath == ".")
            {
                fullPath = candidate;
                return true;
            }

            var currentPath = normalizedRoot;
            foreach (var component in relativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, component);

                try
                {
                    if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                        return false;
                }
                catch (FileNotFoundException)
                {
                    // A missing component means that no deeper component can exist yet.
                    break;
                }
                catch (DirectoryNotFoundException)
                {
                    break;
                }
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a path like <see cref="TryResolveDataPathInsideRoot"/> and returns
    /// an empty string when resolution fails or escapes <see cref="DataRoot"/>.
    /// </summary>
    public static string ResolveDataPathInsideRootOrEmpty(string? path)
    {
        return TryResolveDataPathInsideRoot(path, out var fullPath)
            ? fullPath
            : string.Empty;
    }

    /// <summary>
    /// Returns true when <paramref name="candidatePath"/> is equal to DataRoot
    /// or located under DataRoot.
    /// </summary>
    public static bool IsPathInsideDataRoot(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            var normalizedRoot = Path.GetFullPath(DataRoot);
            var normalizedCandidate = Path.GetFullPath(candidatePath);

            var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            return FileSystemPathIdentity.Equals(normalizedCandidate, normalizedRoot) ||
                   normalizedCandidate.StartsWith(rootWithSeparator, FileSystemPathIdentity.Comparison);
        }
        catch
        {
            return false;
        }
    }
}
