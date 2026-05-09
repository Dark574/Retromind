using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Retromind.Models;

namespace Retromind.Helpers;

public static class NodeAssetFolderHelper
{
    private static readonly AssetType[] AssetFolderTypes = Enum.GetValues(typeof(AssetType))
        .Cast<AssetType>()
        .Where(type => type != AssetType.Unknown)
        .ToArray();

    private static readonly Regex AssetFileRegex = new Regex(
        @"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music|Banner|Bezel|ControlPanel|Manual|Screenshot)_(\d+)\..*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void UpdateAssetPathsRecursive(
        MediaNode node,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        UpdateNodeAssetPaths(node, oldPrefix, newPrefix, renamedFiles);

        foreach (var item in node.Items)
        {
            UpdateAssetPaths(item.Assets, oldPrefix, newPrefix, renamedFiles);

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
        }

        foreach (var child in node.Children)
        {
            UpdateAssetPathsRecursive(child, oldPrefix, newPrefix, renamedFiles);
        }
    }

    public static bool HasAnyAssetFolders(string nodeFolder)
    {
        if (string.IsNullOrWhiteSpace(nodeFolder))
            return false;

        if (!Directory.Exists(nodeFolder))
            return false;

        foreach (var type in AssetFolderTypes)
        {
            var folder = Path.Combine(nodeFolder, type.ToString());
            if (Directory.Exists(folder))
                return true;
        }

        return false;
    }

    public static bool MoveAssetFoldersRecursive(
        MediaNode node,
        List<string> oldBaseSegments,
        List<string> newBaseSegments,
        Dictionary<string, string> renamedFiles)
    {
        var relativeSegments = new List<string>();
        return MoveAssetFoldersRecursive(node, oldBaseSegments, newBaseSegments, relativeSegments, renamedFiles);
    }

    public static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            if (Directory.EnumerateFileSystemEntries(path).Any())
                return;

            Directory.Delete(path);
        }
        catch
        {
            // best effort cleanup
        }
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').Trim();

        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = normalized.TrimStart('/');

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static void UpdateNodeAssetPaths(
        MediaNode node,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        var activeByType = new Dictionary<AssetType, string?>();
        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown)
                continue;

            activeByType[type] = node.GetPrimaryAssetPath(type);
        }

        UpdateAssetPaths(node.Assets, oldPrefix, newPrefix, renamedFiles);

        foreach (var kvp in activeByType)
        {
            var activePath = kvp.Value;
            if (string.IsNullOrWhiteSpace(activePath))
                continue;

            var updated = TryMapRenamedPath(activePath, renamedFiles, out var mapped)
                ? mapped
                : ReplaceRelativePrefix(activePath, oldPrefix, newPrefix);
            if (!string.Equals(updated, activePath, StringComparison.OrdinalIgnoreCase))
                node.SetActiveAsset(kvp.Key, updated);
        }
    }

    private static void UpdateAssetPaths(
        IEnumerable<MediaAsset> assets,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            if (TryMapRenamedPath(asset.RelativePath, renamedFiles, out var mapped))
                asset.RelativePath = mapped;
            else
                asset.RelativePath = ReplaceRelativePrefix(asset.RelativePath, oldPrefix, newPrefix);
        }
    }

    private static bool TryMapRenamedPath(
        string path,
        IReadOnlyDictionary<string, string>? renamedFiles,
        out string mapped)
    {
        mapped = string.Empty;

        if (renamedFiles == null || renamedFiles.Count == 0)
            return false;

        var normalized = NormalizeRelativePath(path);
        if (!renamedFiles.TryGetValue(normalized, out var mappedValue))
            return false;

        mapped = mappedValue;
        return true;
    }

    private static bool MoveAssetFoldersRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        IReadOnlyList<string> newBaseSegments,
        List<string> relativeSegments,
        Dictionary<string, string> renamedFiles)
    {
        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        var newSegments = new List<string>(newBaseSegments.Count + relativeSegments.Count);
        newSegments.AddRange(newBaseSegments);
        newSegments.AddRange(relativeSegments);

        if (!MoveAssetFoldersForNode(oldSegments, newSegments, renamedFiles))
            return false;

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            if (!MoveAssetFoldersRecursive(child, oldBaseSegments, newBaseSegments, relativeSegments, renamedFiles))
                return false;
            relativeSegments.RemoveAt(relativeSegments.Count - 1);
        }

        var oldFolder = PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot);
        DeleteDirectoryIfEmpty(oldFolder);

        return true;
    }

    private static bool MoveAssetFoldersForNode(
        List<string> oldSegments,
        List<string> newSegments,
        Dictionary<string, string> renamedFiles)
    {
        var oldFolder = PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot);
        if (!Directory.Exists(oldFolder))
            return true;

        var newFolder = PathHelper.ResolveNodeFolder(newSegments, AppPaths.LibraryRoot);

        foreach (var type in AssetFolderTypes)
        {
            var oldTypeFolder = Path.Combine(oldFolder, type.ToString());
            if (!Directory.Exists(oldTypeFolder))
                continue;

            var newTypeFolder = Path.Combine(newFolder, type.ToString());

            if (!Directory.Exists(newTypeFolder))
            {
                var newParentDir = Path.GetDirectoryName(newTypeFolder);
                if (!string.IsNullOrWhiteSpace(newParentDir) && !Directory.Exists(newParentDir))
                    Directory.CreateDirectory(newParentDir);

                Directory.Move(oldTypeFolder, newTypeFolder);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(oldTypeFolder))
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var targetPath = Path.Combine(newTypeFolder, fileName);
                string? mappedOld = null;
                string? mappedNew = null;
                if (File.Exists(targetPath))
                {
                    targetPath = GetRenumberedAssetPath(newTypeFolder, fileName);
                    mappedOld = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, file));
                    mappedNew = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, targetPath));
                }

                File.Move(file, targetPath);

                if (!string.IsNullOrWhiteSpace(mappedOld) &&
                    !string.IsNullOrWhiteSpace(mappedNew) &&
                    !string.Equals(mappedOld, mappedNew, StringComparison.OrdinalIgnoreCase))
                {
                    if (renamedFiles.TryGetValue(mappedOld, out var existing) &&
                        !string.Equals(existing, mappedNew, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine(
                            $"[NodeAssetFolderHelper] Renamed path collision for '{mappedOld}': '{existing}' -> '{mappedNew}'. Overwriting mapping.");
                    }

                    renamedFiles[mappedOld] = mappedNew;
                }
            }

            DeleteDirectoryIfEmpty(oldTypeFolder);
        }

        return true;
    }

    private static string GetRenumberedAssetPath(string targetFolder, string fileName)
    {
        var match = AssetFileRegex.Match(fileName);
        if (match.Success)
        {
            var baseTitle = match.Groups[1].Value;
            var typeToken = match.Groups[2].Value;
            var extension = Path.GetExtension(fileName);
            var prefix = $"{baseTitle}_{typeToken}_";
            var next = GetNextAssetNumber(targetFolder, prefix);
            return GetUniqueNameWithPrefix(targetFolder, prefix, extension, next);
        }

        return GetFallbackRenamedPath(targetFolder, fileName);
    }

    private static int GetNextAssetNumber(string targetFolder, string prefix)
    {
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(targetFolder))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = name.Substring(prefix.Length);
            var dotIndex = remainder.IndexOf('.');
            if (dotIndex <= 0)
                continue;

            var numberPart = remainder.Substring(0, dotIndex);
            if (int.TryParse(numberPart, out var number) && number > max)
                max = number;
        }

        return max + 1;
    }

    private static string GetUniqueNameWithPrefix(string targetFolder, string prefix, string extension, int startNumber)
    {
        var counter = Math.Max(startNumber, 1);
        while (true)
        {
            var name = $"{prefix}{counter:D2}{extension}";
            var candidate = Path.Combine(targetFolder, name);
            if (!File.Exists(candidate))
                return candidate;

            counter++;
        }
    }

    private static string GetFallbackRenamedPath(string targetFolder, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (true)
        {
            var candidateName = $"{baseName}_Moved_{counter:D2}{extension}";
            var candidatePath = Path.Combine(targetFolder, candidateName);
            if (!File.Exists(candidatePath))
                return candidatePath;

            counter++;
        }
    }

    private static string ReplaceRelativePrefix(string path, string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalizedPath = NormalizeRelativePath(path);
        var normalizedOld = NormalizeRelativePath(oldPrefix);
        var normalizedNew = NormalizeRelativePath(newPrefix);

        if (string.Equals(normalizedPath, normalizedOld, StringComparison.OrdinalIgnoreCase))
            return normalizedNew;

        var oldWithSlash = normalizedOld.EndsWith("/", StringComparison.Ordinal) ? normalizedOld : normalizedOld + "/";
        if (normalizedPath.StartsWith(oldWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedNewWithSlash = normalizedNew.EndsWith("/", StringComparison.Ordinal)
                ? normalizedNew
                : normalizedNew + "/";
            return normalizedNewWithSlash + normalizedPath.Substring(oldWithSlash.Length);
        }

        return path;
    }
}
