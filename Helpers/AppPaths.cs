using System;
using System.IO;
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
        }
        catch
        {
            // best-effort; themes must never break startup
        }
    }

    private static void EnsurePortableThemeDirectory(string shippedDir, string targetDir)
    {
        try
        {
            if (!Directory.Exists(targetDir))
            {
                CopyDirectoryRecursive(shippedDir, targetDir);
                WriteThemeManifest(targetDir, shippedDir);
                return;
            }

            var manifest = TryReadThemeManifest(targetDir);
            if (manifest == null)
                return;

            var targetHash = ComputeDirectoryHash(targetDir);
            if (!string.Equals(targetHash, manifest.InstalledHash, StringComparison.OrdinalIgnoreCase))
                return; // User modified the theme locally.

            var shippedHash = ComputeDirectoryHash(shippedDir);
            if (string.Equals(shippedHash, manifest.SourceHash, StringComparison.OrdinalIgnoreCase))
                return; // No shipped updates.

            Directory.Delete(targetDir, recursive: true);
            CopyDirectoryRecursive(shippedDir, targetDir);
            WriteThemeManifest(targetDir, shippedDir);
        }
        catch
        {
            // best-effort; never break startup due to theme sync
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
        catch
        {
            // best-effort
        }
    }

    private static string ComputeDirectoryHash(string directory)
    {
        using var sha = SHA256.Create();
        var separator = new byte[] { 0 };
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        // Use file metadata to avoid hashing large theme assets at startup.
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), ThemeManifestFileName, StringComparison.OrdinalIgnoreCase))
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
    /// Converts an absolute path to a DataRoot-relative path (portable).
    /// If the path is already relative, it is returned normalized.
    /// </summary>
    public static string MakeDataRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.IsPathRooted(path)
            ? Path.GetRelativePath(DataRoot, path)
            : path;
    }
}
