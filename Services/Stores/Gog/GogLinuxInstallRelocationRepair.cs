using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Retromind.Services.Stores.Gog;

internal static partial class GogLinuxInstallRelocationRepair
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    [GeneratedRegex("<product\\b[^>]*\\broot=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex XmlRootRegex();

    [GeneratedRegex("\\broot\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex LuaRootRegex();

    public static int RepairFromManifest(string installRoot)
    {
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var manifestDirectory = Path.Combine(fullInstallRoot, ".mojosetup", "manifest");
        if (!Directory.Exists(manifestDirectory))
            return 0;

        var previousRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in Directory.EnumerateFiles(manifestDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(manifestPath);
            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(manifestPath);
            var match = extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? XmlRootRegex().Match(text)
                : LuaRootRegex().Match(text);
            if (!match.Success)
                continue;

            var previousRoot = extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? WebUtility.HtmlDecode(match.Groups[1].Value)
                : match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(previousRoot) &&
                !PathsEqual(previousRoot, fullInstallRoot))
            {
                previousRoots.Add(previousRoot);
            }
        }

        var changedFiles = 0;
        foreach (var previousRoot in previousRoots)
            changedFiles += RepairMovedInstallation(fullInstallRoot, previousRoot);

        return changedFiles;
    }

    public static int RepairMovedInstallation(string installRoot, string previousRoot)
    {
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var fullPreviousRoot = Path.GetFullPath(previousRoot);
        if (PathsEqual(fullInstallRoot, fullPreviousRoot))
            return 0;

        var mojoSetupDirectory = Path.Combine(fullInstallRoot, ".mojosetup");
        if (!Directory.Exists(mojoSetupDirectory))
            return 0;

        var changedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(mojoSetupDirectory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".lua", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".desktop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            var updated = text.Replace(fullPreviousRoot, fullInstallRoot, StringComparison.Ordinal);
            var encodedPreviousRoot = WebUtility.HtmlEncode(fullPreviousRoot);
            if (!string.Equals(encodedPreviousRoot, fullPreviousRoot, StringComparison.Ordinal))
            {
                updated = updated.Replace(
                    encodedPreviousRoot,
                    WebUtility.HtmlEncode(fullInstallRoot),
                    StringComparison.Ordinal);
            }

            if (string.Equals(text, updated, StringComparison.Ordinal))
                continue;

            WriteAllTextAtomic(path, updated);
            changedFiles++;
        }

        return changedFiles;
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var tempPath = path + $".retromind-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, Utf8WithoutBom);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
