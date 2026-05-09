using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static class NodeAssetFolderHelper
{
    private sealed class PlannedFileMove
    {
        public required string SourcePath { get; init; }
        public required string TargetPath { get; init; }
        public string? StagingPath { get; set; }
    }

    private static readonly AssetType[] AssetFolderTypes = Enum.GetValues(typeof(AssetType))
        .Cast<AssetType>()
        .Where(type => type != AssetType.Unknown)
        .ToArray();

    private static readonly HashSet<string> AssetTypeTokens = new HashSet<string>(
        AssetFolderTypes.Select(type => type.ToString()),
        StringComparer.OrdinalIgnoreCase);

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
        var plannedMoves = new List<PlannedFileMove>();
        var plannedRenamedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reservedNamesByFolder = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (!TryPlanAssetFolderMovesRecursive(
                node,
                oldBaseSegments,
                newBaseSegments,
                relativeSegments,
                plannedMoves,
                plannedRenamedFiles,
                reservedNamesByFolder))
        {
            return false;
        }

        if (plannedMoves.Count == 0)
        {
            CleanupEmptyOldFoldersRecursive(node, oldBaseSegments, relativeSegments);
            return true;
        }

        if (!TryExecutePlannedMoves(plannedMoves, plannedRenamedFiles, renamedFiles))
            return false;

        CleanupEmptyOldFoldersRecursive(node, oldBaseSegments, relativeSegments);
        return true;
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

    private static bool TryPlanAssetFolderMovesRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        IReadOnlyList<string> newBaseSegments,
        List<string> relativeSegments,
        List<PlannedFileMove> plannedMoves,
        Dictionary<string, string> plannedRenamedFiles,
        Dictionary<string, HashSet<string>> reservedNamesByFolder)
    {
        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        var newSegments = new List<string>(newBaseSegments.Count + relativeSegments.Count);
        newSegments.AddRange(newBaseSegments);
        newSegments.AddRange(relativeSegments);

        if (!TryPlanAssetFolderMovesForNode(
                oldSegments,
                newSegments,
                plannedMoves,
                plannedRenamedFiles,
                reservedNamesByFolder))
        {
            return false;
        }

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            if (!TryPlanAssetFolderMovesRecursive(
                    child,
                    oldBaseSegments,
                    newBaseSegments,
                    relativeSegments,
                    plannedMoves,
                    plannedRenamedFiles,
                    reservedNamesByFolder))
            {
                return false;
            }

            relativeSegments.RemoveAt(relativeSegments.Count - 1);
        }

        return true;
    }

    private static bool TryPlanAssetFolderMovesForNode(
        List<string> oldSegments,
        List<string> newSegments,
        List<PlannedFileMove> plannedMoves,
        Dictionary<string, string> plannedRenamedFiles,
        Dictionary<string, HashSet<string>> reservedNamesByFolder)
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
            if (string.Equals(oldTypeFolder, newTypeFolder, StringComparison.OrdinalIgnoreCase))
                continue;

            var reservedNames = GetReservedFileNames(newTypeFolder, reservedNamesByFolder);

            foreach (var file in Directory.EnumerateFiles(oldTypeFolder))
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var targetFileName = fileName;
                string? renamedRelativePath = null;

                if (!reservedNames.Add(targetFileName))
                {
                    targetFileName = GetRenumberedAssetFileName(fileName, reservedNames);
                    if (string.IsNullOrWhiteSpace(targetFileName))
                    {
                        Debug.WriteLine($"[NodeAssetFolderHelper] Failed to allocate renamed target for '{file}'.");
                        return false;
                    }
                }

                var targetPath = Path.Combine(newTypeFolder, targetFileName);
                var sourceRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, file));

                if (!string.Equals(targetFileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    renamedRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, targetPath));
                    if (plannedRenamedFiles.TryGetValue(sourceRelativePath, out var existing) &&
                        !string.Equals(existing, renamedRelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine(
                            $"[NodeAssetFolderHelper] Planned rename collision for '{sourceRelativePath}': '{existing}' -> '{renamedRelativePath}'. Overwriting mapping.");
                    }

                    plannedRenamedFiles[sourceRelativePath] = renamedRelativePath;
                }

                plannedMoves.Add(new PlannedFileMove
                {
                    SourcePath = file,
                    TargetPath = targetPath
                });
            }
        }

        return true;
    }

    private static HashSet<string> GetReservedFileNames(
        string targetFolder,
        Dictionary<string, HashSet<string>> reservedNamesByFolder)
    {
        if (reservedNamesByFolder.TryGetValue(targetFolder, out var existing))
            return existing;

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(targetFolder))
        {
            foreach (var existingFile in Directory.EnumerateFiles(targetFolder))
            {
                var existingName = Path.GetFileName(existingFile);
                if (!string.IsNullOrWhiteSpace(existingName))
                    reserved.Add(existingName);
            }
        }

        reservedNamesByFolder[targetFolder] = reserved;
        return reserved;
    }

    private static bool TryExecutePlannedMoves(
        List<PlannedFileMove> plannedMoves,
        IReadOnlyDictionary<string, string> plannedRenamedFiles,
        Dictionary<string, string> renamedFiles)
    {
        var stagingRoot = Path.Combine(
            AppPaths.LibraryRoot,
            ".retromind_asset_move_staging",
            Guid.NewGuid().ToString("N"));

        var stagedMoves = new List<PlannedFileMove>(plannedMoves.Count);
        var committedMoves = new List<PlannedFileMove>(plannedMoves.Count);

        try
        {
            Directory.CreateDirectory(stagingRoot);

            for (var i = 0; i < plannedMoves.Count; i++)
            {
                var move = plannedMoves[i];
                if (!File.Exists(move.SourcePath))
                    throw new FileNotFoundException("Source file does not exist for planned asset move.", move.SourcePath);

                var extension = Path.GetExtension(move.SourcePath);
                var stagePath = Path.Combine(stagingRoot, $"{i:D6}{extension}");

                File.Move(move.SourcePath, stagePath);
                move.StagingPath = stagePath;
                stagedMoves.Add(move);
            }

            foreach (var move in plannedMoves)
            {
                if (string.IsNullOrWhiteSpace(move.StagingPath))
                    throw new InvalidOperationException($"Staging path missing for planned move '{move.SourcePath}'.");

                var targetDirectory = Path.GetDirectoryName(move.TargetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                if (File.Exists(move.TargetPath))
                    throw new IOException($"Target collision during commit: '{move.TargetPath}'.");

                File.Move(move.StagingPath, move.TargetPath);
                committedMoves.Add(move);
            }

            foreach (var kvp in plannedRenamedFiles)
            {
                if (renamedFiles.TryGetValue(kvp.Key, out var existing) &&
                    !string.Equals(existing, kvp.Value, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Renamed path collision for '{kvp.Key}': '{existing}' -> '{kvp.Value}'. Overwriting mapping.");
                }

                renamedFiles[kvp.Key] = kvp.Value;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NodeAssetFolderHelper] Atomic move failed, rolling back: {ex}");
            RollbackPlannedMoves(stagedMoves, committedMoves);
            return false;
        }
        finally
        {
            DeleteDirectoryIfEmpty(stagingRoot);

            var stagingParent = Path.GetDirectoryName(stagingRoot);
            if (!string.IsNullOrWhiteSpace(stagingParent))
                DeleteDirectoryIfEmpty(stagingParent);
        }
    }

    private static void RollbackPlannedMoves(
        IReadOnlyList<PlannedFileMove> stagedMoves,
        IReadOnlyList<PlannedFileMove> committedMoves)
    {
        for (var i = committedMoves.Count - 1; i >= 0; i--)
        {
            var move = committedMoves[i];
            try
            {
                if (!File.Exists(move.TargetPath))
                    continue;

                var sourceDirectory = Path.GetDirectoryName(move.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && !Directory.Exists(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (File.Exists(move.SourcePath))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Rollback source already exists for '{move.SourcePath}'. Keeping committed target in place.");
                    continue;
                }

                File.Move(move.TargetPath, move.SourcePath);
            }
            catch (Exception rollbackEx)
            {
                Debug.WriteLine($"[NodeAssetFolderHelper] Rollback failed (commit phase): {rollbackEx}");
            }
        }

        for (var i = stagedMoves.Count - 1; i >= 0; i--)
        {
            var move = stagedMoves[i];
            try
            {
                if (string.IsNullOrWhiteSpace(move.StagingPath) || !File.Exists(move.StagingPath))
                    continue;

                var sourceDirectory = Path.GetDirectoryName(move.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && !Directory.Exists(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (File.Exists(move.SourcePath))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Rollback source already exists for staged move '{move.SourcePath}'.");
                    continue;
                }

                File.Move(move.StagingPath, move.SourcePath);
            }
            catch (Exception rollbackEx)
            {
                Debug.WriteLine($"[NodeAssetFolderHelper] Rollback failed (staging phase): {rollbackEx}");
            }
        }
    }

    private static void CleanupEmptyOldFoldersRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        List<string> relativeSegments)
    {
        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            CleanupEmptyOldFoldersRecursive(child, oldBaseSegments, relativeSegments);
            relativeSegments.RemoveAt(relativeSegments.Count - 1);
        }

        var oldFolder = PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot);
        foreach (var type in AssetFolderTypes)
            DeleteDirectoryIfEmpty(Path.Combine(oldFolder, type.ToString()));

        DeleteDirectoryIfEmpty(oldFolder);
    }

    private static string GetRenumberedAssetFileName(string fileName, HashSet<string> reservedNames)
    {
        if (TryParseNumberedAssetName(fileName, out var baseTitle, out var typeToken))
        {
            var extension = Path.GetExtension(fileName);
            var prefix = $"{baseTitle}_{typeToken}_";
            var next = GetNextAssetNumber(reservedNames, prefix);
            return GetUniqueNameWithPrefix(reservedNames, prefix, extension, next);
        }

        return GetFallbackRenamedFileName(fileName, reservedNames);
    }

    private static bool TryParseNumberedAssetName(string fileName, out string baseTitle, out string typeToken)
    {
        baseTitle = string.Empty;
        typeToken = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
            return false;

        var lastUnderscore = nameWithoutExtension.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore >= nameWithoutExtension.Length - 1)
            return false;

        var numberPart = nameWithoutExtension[(lastUnderscore + 1)..];
        if (!int.TryParse(numberPart, out var number) || number < 0)
            return false;

        var beforeNumber = nameWithoutExtension[..lastUnderscore];
        var secondLastUnderscore = beforeNumber.LastIndexOf('_');
        if (secondLastUnderscore <= 0 || secondLastUnderscore >= beforeNumber.Length - 1)
            return false;

        var parsedTypeToken = beforeNumber[(secondLastUnderscore + 1)..];
        if (!AssetTypeTokens.Contains(parsedTypeToken))
            return false;

        baseTitle = beforeNumber[..secondLastUnderscore];
        if (string.IsNullOrWhiteSpace(baseTitle))
            return false;

        typeToken = parsedTypeToken;
        return true;
    }

    private static int GetNextAssetNumber(HashSet<string> reservedNames, string prefix)
    {
        var max = 0;
        foreach (var name in reservedNames)
        {
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

    private static string GetUniqueNameWithPrefix(
        HashSet<string> reservedNames,
        string prefix,
        string extension,
        int startNumber)
    {
        var counter = Math.Max(startNumber, 1);
        while (true)
        {
            var name = $"{prefix}{counter:D2}{extension}";
            if (reservedNames.Add(name))
                return name;

            counter++;
        }
    }

    private static string GetFallbackRenamedFileName(string fileName, HashSet<string> reservedNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (true)
        {
            var candidateName = $"{baseName}_Moved_{counter:D2}{extension}";
            if (reservedNames.Add(candidateName))
                return candidateName;

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
