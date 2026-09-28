using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

public enum LibraryHealthIssueKind
{
    MissingAsset,
    OrphanedAsset,
    MissingLaunchFile,
    InconsistentEntry
}

public enum LibraryHealthIssueReason
{
    AssetFileMissing,
    OrphanedAssetFile,
    LaunchFileMissing,
    EmptyAssetPath,
    InvalidAssetPath,
    DuplicateAssetReference,
    EmptyLaunchPath,
    InvalidLaunchPath,
    MissingNodeId,
    DuplicateNodeId,
    MissingItemId,
    DuplicateItemId,
    AssetFolderUnreadable
}

public sealed record LibraryHealthIssue(
    LibraryHealthIssueKind Kind,
    LibraryHealthIssueReason Reason,
    string Owner,
    string NodePath,
    string Location,
    long? SizeBytes = null);

public sealed record LibraryHealthReport(
    IReadOnlyList<LibraryHealthIssue> Issues,
    int NodeCount,
    int ItemCount,
    int AssetReferenceCount,
    int AssetFileCount);

/// <summary>
/// Performs a read-only consistency check between the loaded library tree and
/// Retromind-managed asset folders. It deliberately never traverses game,
/// prefix, installer, theme, cache, or backup trees.
/// </summary>
public sealed class LibraryHealthService
{
    private static readonly AssetType[] ManagedAssetTypes = Enum.GetValues<AssetType>()
        .Where(static type => type != AssetType.Unknown)
        .ToArray();

    private readonly string _dataRoot;
    private readonly string _libraryRoot;
    private readonly StringComparer _pathComparer;

    public LibraryHealthService()
        : this(AppPaths.DataRoot, AppPaths.LibraryRoot)
    {
    }

    internal LibraryHealthService(string dataRoot, string libraryRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _libraryRoot = Path.GetFullPath(libraryRoot);
        _pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    public Task<LibraryHealthReport> ScanAsync(
        IEnumerable<MediaNode> rootNodes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootNodes);

        // The observable tree belongs to the UI thread. Capture only immutable
        // values here, then perform all filesystem work on a background thread.
        var snapshot = CreateSnapshot(rootNodes, cancellationToken);
        return Task.Run(() => ScanCore(snapshot, cancellationToken), cancellationToken);
    }

    private LibrarySnapshot CreateSnapshot(
        IEnumerable<MediaNode> rootNodes,
        CancellationToken cancellationToken)
    {
        var nodes = new List<NodeSnapshot>();
        foreach (var root in rootNodes)
            CaptureNode(root, Array.Empty<string>(), nodes, cancellationToken);

        return new LibrarySnapshot(nodes);
    }

    private void CaptureNode(
        MediaNode node,
        IReadOnlyList<string> parentPath,
        ICollection<NodeSnapshot> nodes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = parentPath.Append(node.Name ?? string.Empty).ToArray();
        var nodeFolder = PathHelper.ResolveNodeFolder(path.ToList(), _libraryRoot);
        var items = node.Items.Select(item => new ItemSnapshot(
            item.Id ?? string.Empty,
            item.Title ?? string.Empty,
            item.MediaType,
            item.LauncherArgs ?? string.Empty,
            item.Assets.Select(CreateAssetSnapshot).ToArray(),
            item.Files.Select(file => new FileSnapshot(file.Kind, file.Path ?? string.Empty)).ToArray()))
            .ToArray();

        nodes.Add(new NodeSnapshot(
            node.Id ?? string.Empty,
            node.Name ?? string.Empty,
            string.Join(" / ", path.Where(static segment => !string.IsNullOrWhiteSpace(segment))),
            nodeFolder,
            node.Assets.Select(CreateAssetSnapshot).ToArray(),
            items));

        foreach (var child in node.Children)
            CaptureNode(child, path, nodes, cancellationToken);
    }

    private static AssetSnapshot CreateAssetSnapshot(MediaAsset asset) =>
        new(asset.Type, asset.RelativePath ?? string.Empty);

    private LibraryHealthReport ScanCore(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var issues = new List<LibraryHealthIssue>();
        var referencedAssets = new HashSet<string>(_pathComparer);
        var scannedAssetFolders = new HashSet<string>(_pathComparer);
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemCount = 0;
        var assetReferenceCount = 0;
        var assetFileCount = 0;

        foreach (var node in snapshot.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckIdentity(node.Id, node.Name, node.NodePath, isNode: true, nodeIds, issues);
            CheckAssets(node.Assets, node.Name, node.NodePath, referencedAssets, issues, ref assetReferenceCount);

            foreach (var item in node.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                itemCount++;
                CheckIdentity(item.Id, item.Title, node.NodePath, isNode: false, itemIds, issues);
                CheckAssets(item.Assets, item.Title, node.NodePath, referencedAssets, issues, ref assetReferenceCount);
                CheckLaunchFiles(item, node.NodePath, issues);
            }
        }

        foreach (var node in snapshot.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var type in ManagedAssetTypes)
            {
                var folder = Path.GetFullPath(Path.Combine(node.NodeFolder, type.ToString()));
                if (!scannedAssetFolders.Add(folder) || !Directory.Exists(folder))
                    continue;

                if (IsSymbolicLink(folder))
                {
                    issues.Add(new LibraryHealthIssue(
                        LibraryHealthIssueKind.InconsistentEntry,
                        LibraryHealthIssueReason.AssetFolderUnreadable,
                        node.Name,
                        node.NodePath,
                        folder));
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var normalized = Path.GetFullPath(file);
                        assetFileCount++;
                        if (referencedAssets.Contains(normalized))
                            continue;

                        issues.Add(new LibraryHealthIssue(
                            LibraryHealthIssueKind.OrphanedAsset,
                            LibraryHealthIssueReason.OrphanedAssetFile,
                            node.Name,
                            node.NodePath,
                            normalized,
                            TryGetFileSize(normalized)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new LibraryHealthIssue(
                        LibraryHealthIssueKind.InconsistentEntry,
                        LibraryHealthIssueReason.AssetFolderUnreadable,
                        node.Name,
                        node.NodePath,
                        folder));
                }
            }
        }

        return new LibraryHealthReport(
            issues.OrderBy(static issue => issue.Kind)
                .ThenBy(static issue => issue.NodePath, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static issue => issue.Owner, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static issue => issue.Location, StringComparer.Ordinal)
                .ToArray(),
            snapshot.Nodes.Count,
            itemCount,
            assetReferenceCount,
            assetFileCount);
    }

    private void CheckAssets(
        IReadOnlyList<AssetSnapshot> assets,
        string owner,
        string nodePath,
        ISet<string> referencedAssets,
        ICollection<LibraryHealthIssue> issues,
        ref int assetReferenceCount)
    {
        var ownerReferences = new HashSet<(AssetType Type, string Path)>();
        foreach (var asset in assets)
        {
            assetReferenceCount++;
            if (string.IsNullOrWhiteSpace(asset.Path))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.InconsistentEntry,
                    LibraryHealthIssueReason.EmptyAssetPath,
                    owner,
                    nodePath,
                    asset.Type.ToString()));
                continue;
            }

            if (!TryResolveDataPath(asset.Path, out var fullPath))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.InconsistentEntry,
                    LibraryHealthIssueReason.InvalidAssetPath,
                    owner,
                    nodePath,
                    asset.Path));
                continue;
            }

            referencedAssets.Add(fullPath);
            if (!ownerReferences.Add((asset.Type, fullPath)))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.InconsistentEntry,
                    LibraryHealthIssueReason.DuplicateAssetReference,
                    owner,
                    nodePath,
                    fullPath));
            }

            if (!File.Exists(fullPath))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.MissingAsset,
                    LibraryHealthIssueReason.AssetFileMissing,
                    owner,
                    nodePath,
                    fullPath));
            }
        }
    }

    private void CheckLaunchFiles(
        ItemSnapshot item,
        string nodePath,
        ICollection<LibraryHealthIssue> issues)
    {
        foreach (var file in item.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.InconsistentEntry,
                    LibraryHealthIssueReason.EmptyLaunchPath,
                    item.Title,
                    nodePath,
                    string.Empty));
                continue;
            }

            // Store and browser protocol targets are launchable identifiers, not files.
            if (Uri.TryCreate(file.Path, UriKind.Absolute, out var uri) && !uri.IsFile)
                continue;

            // Command items may intentionally store a PATH-resolved executable token
            // such as "steam", "heroic" or "flatpak" in Files. Availability of
            // that command belongs to launch diagnostics, not JSON path integrity.
            if (IsCommandToken(file.Path) &&
                (item.MediaType == MediaType.Command || LooksLikeUriOrProtocol(item.LauncherArgs)))
                continue;

            string fullPath;
            try
            {
                fullPath = file.Kind switch
                {
                    MediaFileKind.LibraryRelative when TryResolveDataPath(file.Path, out var resolved) => resolved,
                    MediaFileKind.Absolute when Path.IsPathRooted(file.Path) => Path.GetFullPath(file.Path),
                    _ => string.Empty
                };
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                fullPath = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.InconsistentEntry,
                    LibraryHealthIssueReason.InvalidLaunchPath,
                    item.Title,
                    nodePath,
                    file.Path));
            }
            else if (!File.Exists(fullPath))
            {
                issues.Add(new LibraryHealthIssue(
                    LibraryHealthIssueKind.MissingLaunchFile,
                    LibraryHealthIssueReason.LaunchFileMissing,
                    item.Title,
                    nodePath,
                    fullPath));
            }
        }
    }

    private static void CheckIdentity(
        string id,
        string owner,
        string nodePath,
        bool isNode,
        IDictionary<string, string> identities,
        ICollection<LibraryHealthIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            issues.Add(new LibraryHealthIssue(
                LibraryHealthIssueKind.InconsistentEntry,
                isNode ? LibraryHealthIssueReason.MissingNodeId : LibraryHealthIssueReason.MissingItemId,
                owner,
                nodePath,
                string.Empty));
            return;
        }

        if (identities.TryAdd(id, owner))
            return;

        issues.Add(new LibraryHealthIssue(
            LibraryHealthIssueKind.InconsistentEntry,
            isNode ? LibraryHealthIssueReason.DuplicateNodeId : LibraryHealthIssueReason.DuplicateItemId,
            owner,
            nodePath,
            id));
    }

    private bool TryResolveDataPath(string storedPath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var candidate = Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.GetFullPath(Path.Combine(_dataRoot, storedPath));
            var rootWithSeparator = Path.EndsInDirectorySeparator(_dataRoot)
                ? _dataRoot
                : _dataRoot + Path.DirectorySeparatorChar;

            if (!_pathComparer.Equals(candidate, _dataRoot) &&
                !candidate.StartsWith(rootWithSeparator, GetPathComparison()))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsCommandToken(string value) =>
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.StartsWith(".", StringComparison.Ordinal);

    private static bool LooksLikeUriOrProtocol(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal))
            return true;

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && !(colon == 1 && char.IsLetter(value[0]));
    }

    private sealed record LibrarySnapshot(IReadOnlyList<NodeSnapshot> Nodes);
    private sealed record NodeSnapshot(
        string Id,
        string Name,
        string NodePath,
        string NodeFolder,
        IReadOnlyList<AssetSnapshot> Assets,
        IReadOnlyList<ItemSnapshot> Items);
    private sealed record ItemSnapshot(
        string Id,
        string Title,
        MediaType MediaType,
        string LauncherArgs,
        IReadOnlyList<AssetSnapshot> Assets,
        IReadOnlyList<FileSnapshot> Files);
    private sealed record AssetSnapshot(AssetType Type, string Path);
    private sealed record FileSnapshot(MediaFileKind Kind, string Path);
}
