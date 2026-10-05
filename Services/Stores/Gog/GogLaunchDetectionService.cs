using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal sealed record GogDetectedLaunchInfo(
    string ExecutablePath,
    string? LaunchArguments,
    string? WorkingDirectory,
    string InstallRoot);

/// <summary>
/// Resolves an installed GOG game's launch configuration from installer metadata,
/// account play tasks, or conservative filesystem heuristics.
/// </summary>
public sealed class GogLaunchDetectionService
{
    internal GogDetectedLaunchInfo? DetectFromLocalMetadata(
        string selectedInstallPath,
        string storeGameId,
        GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(selectedInstallPath) || !Directory.Exists(selectedInstallPath))
            return null;

        var candidates = new List<GogDetectedLaunchInfo>();
        var weakCandidates = new List<GogDetectedLaunchInfo>();

        foreach (var infoFile in EnumerateFilesSafe(selectedInstallPath, "goggame-*.info"))
        {
            var parsed = TryParseLaunchInfoFromGogInfoFile(infoFile, platform);
            if (parsed == null)
                continue;

            if (string.Equals(parsed.Value.RootGameId, storeGameId, StringComparison.OrdinalIgnoreCase))
                candidates.Add(parsed.Value.LaunchInfo);
            else
                weakCandidates.Add(parsed.Value.LaunchInfo);
        }

        var firstValid = candidates.FirstOrDefault(candidate => File.Exists(candidate.ExecutablePath))
                         ?? weakCandidates.FirstOrDefault(candidate => File.Exists(candidate.ExecutablePath));
        if (firstValid != null)
            return firstValid;

        if (platform == GogInstallPlatform.Linux)
        {
            var fallbackStartScript = EnumerateFilesSafe(selectedInstallPath, "start.sh")
                .FirstOrDefault(path => !IsInsideInstallerStaging(path));
            if (!string.IsNullOrWhiteSpace(fallbackStartScript))
            {
                var installRoot = Directory.GetParent(fallbackStartScript)?.FullName ?? selectedInstallPath;
                return new GogDetectedLaunchInfo(
                    fallbackStartScript,
                    null,
                    installRoot,
                    installRoot);
            }
        }

        return candidates.FirstOrDefault() ?? weakCandidates.FirstOrDefault();
    }

    internal GogDetectedLaunchInfo? DetectFromPlayTasks(
        string installRoot,
        GogInstallPlatform platform,
        IReadOnlyList<GogPlayTaskInfo> playTasks)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot) || playTasks.Count == 0)
            return null;

        foreach (var task in playTasks.OrderByDescending(task => task.IsPrimary))
        {
            if (string.IsNullOrWhiteSpace(task.Path))
                continue;

            var executablePath = ResolveExecutablePath(installRoot, task.Path, platform);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                continue;

            if (platform == GogInstallPlatform.Windows &&
                !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var workingDirectory = ResolveWorkingDirectory(installRoot, task.WorkingDirectory)
                                   ?? Path.GetDirectoryName(executablePath);
            return new GogDetectedLaunchInfo(
                executablePath,
                task.Arguments,
                workingDirectory,
                installRoot);
        }

        return null;
    }

    internal GogDetectedLaunchInfo? DetectFromFilesystem(
        string? title,
        string installRoot,
        GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return null;

        return platform == GogInstallPlatform.Windows
            ? DetectWindowsLaunchByFilesystem(title, installRoot)
            : DetectLinuxLaunchByFilesystem(title, installRoot);
    }

    internal GogDetectedLaunchInfo PreferLinuxStartScript(
        GogDetectedLaunchInfo launchInfo,
        string selectedInstallPath)
    {
        if (string.IsNullOrWhiteSpace(selectedInstallPath) || !Directory.Exists(selectedInstallPath))
            return launchInfo;

        var directCandidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(launchInfo.InstallRoot))
            directCandidates.Add(Path.Combine(launchInfo.InstallRoot, "start.sh"));
        directCandidates.Add(Path.Combine(selectedInstallPath, "start.sh"));

        var preferred = directCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(path => File.Exists(path) && !IsInsideInstallerStaging(path));

        if (string.IsNullOrWhiteSpace(preferred))
        {
            preferred = EnumerateFilesSafe(selectedInstallPath, "start.sh")
                .Where(path => File.Exists(path) && !IsInsideInstallerStaging(path))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(preferred))
            return launchInfo;

        return new GogDetectedLaunchInfo(
            preferred,
            null,
            Path.GetDirectoryName(preferred),
            selectedInstallPath);
    }

    private static GogDetectedLaunchInfo? DetectLinuxLaunchByFilesystem(string? title, string installRoot)
    {
        var startScript = EnumerateFilesSafe(installRoot, "start.sh")
            .FirstOrDefault(path => File.Exists(path) && !IsInsideInstallerStaging(path));
        if (!string.IsNullOrWhiteSpace(startScript))
        {
            return new GogDetectedLaunchInfo(
                startScript,
                null,
                Path.GetDirectoryName(startScript),
                installRoot);
        }

        var candidates = new List<string>();
        foreach (var pattern in new[] { "*.sh", "*.x86_64", "*.x86", "*.AppImage" })
        {
            candidates.AddRange(
                EnumerateFilesSafe(installRoot, pattern)
                    .Where(path => File.Exists(path) && !IsInsideInstallerStaging(path)));
        }

        var normalizedTitle = NormalizeForComparison(title);
        var best = candidates
            .Select(path => new { Path = path, Score = ScoreLinuxExecutable(path, normalizedTitle) })
            .Where(entry => entry.Score > int.MinValue)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path.Length)
            .Select(entry => entry.Path)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(best)
            ? null
            : new GogDetectedLaunchInfo(best, null, Path.GetDirectoryName(best), installRoot);
    }

    private static GogDetectedLaunchInfo? DetectWindowsLaunchByFilesystem(string? title, string installRoot)
    {
        var normalizedTitle = NormalizeForComparison(title);
        var best = EnumerateFilesSafe(installRoot, "*.exe")
            .Where(File.Exists)
            .Where(path => !IsInsideInstallerStaging(path))
            .Select(path => new { Path = path, Score = ScoreWindowsExecutable(path, normalizedTitle) })
            .Where(entry => entry.Score > int.MinValue)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path.Length)
            .Select(entry => entry.Path)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(best)
            ? null
            : new GogDetectedLaunchInfo(best, null, Path.GetDirectoryName(best), installRoot);
    }

    private static int ScoreWindowsExecutable(string path, string normalizedTitle)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("unins", StringComparison.Ordinal) ||
            fileName.Contains("uninstall", StringComparison.Ordinal) ||
            fileName.Contains("setup", StringComparison.Ordinal) ||
            fileName.Contains("install", StringComparison.Ordinal) ||
            fileName.Contains("vcredist", StringComparison.Ordinal) ||
            fileName.Contains("dxsetup", StringComparison.Ordinal))
        {
            return int.MinValue;
        }

        var score = 0;
        var normalizedFileName = NormalizeForComparison(fileName);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
            normalizedFileName.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 8;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant();
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}game{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 3;
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 2;
        if (fileName.Contains("launcher", StringComparison.Ordinal))
            score += 1;
        if (normalizedPath.Contains("support", StringComparison.Ordinal) ||
            normalizedPath.Contains("redist", StringComparison.Ordinal) ||
            normalizedPath.Contains("directx", StringComparison.Ordinal))
        {
            score -= 10;
        }

        return score;
    }

    private static int ScoreLinuxExecutable(string path, string normalizedTitle)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("uninstall", StringComparison.Ordinal) ||
            fileName.Contains("installer", StringComparison.Ordinal))
        {
            return int.MinValue;
        }

        var score = fileName.Equals("start.sh", StringComparison.Ordinal) ? 8 : 0;
        var normalizedFileName = NormalizeForComparison(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
            normalizedFileName.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 6;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant();
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}game{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 2;
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 1;

        return score;
    }

    private static (string? RootGameId, GogDetectedLaunchInfo LaunchInfo)? TryParseLaunchInfoFromGogInfoFile(
        string infoFilePath,
        GogInstallPlatform platform)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(infoFilePath));
            var root = json.RootElement;
            var rootGameId = GetJsonString(root, "rootGameId") ?? GetJsonString(root, "gameId");
            if (!root.TryGetProperty("playTasks", out var playTasks) || playTasks.ValueKind != JsonValueKind.Array)
                return null;

            var task = GogPlayTaskParser.SelectPrimary(GogPlayTaskParser.Parse(playTasks));
            if (task == null)
                return null;

            var infoFolder = Directory.GetParent(infoFilePath)?.FullName;
            if (string.IsNullOrWhiteSpace(infoFolder))
                return null;

            var installRoot = infoFolder;
            if (string.Equals(Path.GetFileName(installRoot), "game", StringComparison.OrdinalIgnoreCase))
                installRoot = Directory.GetParent(installRoot)?.FullName ?? installRoot;

            var executablePath = ResolveExecutablePath(installRoot, task.Path, platform);
            if (string.IsNullOrWhiteSpace(executablePath))
                return null;

            return (rootGameId, new GogDetectedLaunchInfo(
                executablePath,
                task.Arguments,
                ResolveWorkingDirectory(installRoot, task.WorkingDirectory),
                installRoot));
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveExecutablePath(
        string installRoot,
        string relativeExecutable,
        GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(relativeExecutable))
            return null;

        var normalizedRelative = relativeExecutable
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (LooksLikeWindowsAbsolutePath(relativeExecutable))
        {
            normalizedRelative = relativeExecutable[2..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else if (Path.IsPathRooted(normalizedRelative))
        {
            return TryResolvePathInsideRoot(installRoot, normalizedRelative, expectDirectory: false);
        }

        normalizedRelative = normalizedRelative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var direct = TryCombineInsideRoot(installRoot, normalizedRelative);
        if (direct == null)
            return null;

        var resolvedDirect = ResolveRelativePathCaseInsensitive(
            installRoot,
            normalizedRelative,
            expectDirectory: false);
        if (!string.IsNullOrWhiteSpace(resolvedDirect))
            return resolvedDirect;

        if (platform == GogInstallPlatform.Linux)
        {
            var relativeBelowGame = Path.Combine("game", normalizedRelative);
            return ResolveRelativePathCaseInsensitive(installRoot, relativeBelowGame, expectDirectory: false)
                   ?? TryCombineInsideRoot(installRoot, relativeBelowGame);
        }

        var fileName = Path.GetFileName(normalizedRelative);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var byFileName = EnumerateFilesSafe(installRoot, fileName)
                .OrderByDescending(candidate =>
                    string.Equals(
                        NormalizeRelativePathForComparison(Path.GetRelativePath(installRoot, candidate)),
                        NormalizeRelativePathForComparison(normalizedRelative),
                        StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => candidate.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(byFileName))
                return byFileName;
        }

        return direct;
    }

    private static string? ResolveWorkingDirectory(string installRoot, string? relativeWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDirectory))
            return null;

        var normalized = relativeWorkingDirectory
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (LooksLikeWindowsAbsolutePath(relativeWorkingDirectory))
        {
            normalized = relativeWorkingDirectory[2..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else if (Path.IsPathRooted(normalized))
        {
            return TryResolvePathInsideRoot(installRoot, normalized, expectDirectory: true);
        }

        normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ResolveRelativePathCaseInsensitive(installRoot, normalized, expectDirectory: true)
               ?? TryCombineInsideRoot(installRoot, normalized);
    }

    private static string? ResolveRelativePathCaseInsensitive(
        string root,
        string relativePath,
        bool expectDirectory)
    {
        try
        {
            var expectedPath = TryCombineInsideRoot(root, relativePath);
            if (expectedPath == null)
                return null;

            if (expectDirectory ? Directory.Exists(expectedPath) : File.Exists(expectedPath))
                return expectedPath;

            var fullRoot = Path.GetFullPath(root);
            var current = fullRoot;
            var segments = Path.GetRelativePath(fullRoot, expectedPath)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                if (!Directory.Exists(current))
                    return null;

                var match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry =>
                        string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(match))
                    return null;

                current = match;
            }

            return expectDirectory
                ? Directory.Exists(current) ? current : null
                : File.Exists(current) ? current : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolvePathInsideRoot(string root, string candidatePath, bool expectDirectory)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullCandidate = Path.GetFullPath(candidatePath);
            if (!IsSubPathOfOrEqual(fullCandidate, fullRoot))
                return null;

            return expectDirectory
                ? Directory.Exists(fullCandidate) ? fullCandidate : null
                : File.Exists(fullCandidate) ? fullCandidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryCombineInsideRoot(string root, string relativePath)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullCandidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            return IsSubPathOfOrEqual(fullCandidate, fullRoot) ? fullCandidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSubPathOfOrEqual(string path, string potentialParentPath)
    {
        if (FileSystemPathIdentity.Equals(path, potentialParentPath))
            return true;

        var parentWithSeparator = Path.EndsInDirectorySeparator(potentialParentPath)
            ? potentialParentPath
            : potentialParentPath + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSeparator, FileSystemPathIdentity.Comparison);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = 0
            };
            return Directory.EnumerateFiles(root, pattern, options).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsInsideInstallerStaging(string path)
        => path.IndexOf(".retromind-gog-installers", FileSystemPathIdentity.Comparison) >= 0;

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static string NormalizeRelativePathForComparison(string path)
        => path
            .Replace('\\', '/')
            .Replace(Path.DirectorySeparatorChar, '/')
            .TrimStart('/');

    private static bool LooksLikeWindowsAbsolutePath(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Length >= 3 &&
           char.IsLetter(path[0]) &&
           path[1] == ':' &&
           (path[2] == '\\' || path[2] == '/');

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }
}
