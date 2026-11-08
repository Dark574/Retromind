using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    private sealed record PathChange(Func<string?> Read, Action<string> Write, string Before, string After);

    private sealed class PreparedPathUpdates
    {
        private readonly List<PathChange> _changes = new();
        private readonly List<PathChange> _attempted = new();
        private readonly HashSet<MediaItem> _changedItems = new(ReferenceEqualityComparer.Instance);
        private readonly List<Func<bool>> _structureChecks = new();

        public void CheckStructure(Func<bool> check) => _structureChecks.Add(check);

        public int Count => _changes.Count;

        public void Add(Func<string?> read, Action<string> write, string oldPrefix, string newPrefix,
            IReadOnlyDictionary<string, string>? renamedFiles)
        {
            var before = read();
            if (string.IsNullOrWhiteSpace(before)) return;
            var after = TryMapRenamedPath(before, renamedFiles, out var mapped)
                ? mapped : ReplaceRelativePrefix(before, oldPrefix, newPrefix);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                _changes.Add(new(read, write, before, after));
        }

        public void AddItem(MediaItem item) => _changedItems.Add(item);

        public void Apply()
        {
            // Detect intervening model edits before modifying any of the prepared paths.
            if (_structureChecks.Any(check => !check()) || _changes.Any(change => change.Read() != change.Before))
                throw new InvalidOperationException("An asset reference changed after preparation.");
            foreach (var change in _changes)
            {
                _attempted.Add(change); // A setter may assign its value and then throw from a subscriber.
                change.Write(change.After);
            }
            foreach (var item in _changedItems) item.NotifyAssetPathsChanged();
            if (_structureChecks.Any(check => !check()))
                throw new InvalidOperationException("The category structure changed during path publication.");
        }

        public void Rollback(NodeAssetOperationResult result)
        {
            foreach (var change in _attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var current = change.Read();
                    if (current != change.Before && current != change.After)
                        throw new InvalidOperationException("A competing asset reference was preserved.");
                    if (current != change.Before) change.Write(change.Before);
                }
                catch
                {
                    result.MetadataRecoveryRequired = true;
                    result.RollbackComplete = false;
                }
            }
            try
            {
                if (_structureChecks.Any(check => !check()))
                    throw new InvalidOperationException("Concurrent metadata changes require manual recovery.");
            }
            catch { result.MetadataRecoveryRequired = true; result.RollbackComplete = false; }
        }

        public object RecoveryReferences => _changes.Select(change => new
        {
            Before = RelativeLocation(change.Before), After = RelativeLocation(change.After),
            Current = change.Read() is { } current ? RelativeLocation(current) : null
        }).ToArray();
    }

    private static PreparedPathUpdates PreparePathUpdates(IReadOnlyList<MediaNode> nodes, string oldPrefix,
        string newPrefix, IReadOnlyDictionary<string, string>? renamedFiles)
    {
        var result = new PreparedPathUpdates();
        var seenAssets = new HashSet<MediaAsset>(ReferenceEqualityComparer.Instance);
        var seenItems = new HashSet<MediaItem>(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            var children = node.Children.ToArray();
            var items = node.Items.ToArray();
            var assets = node.Assets.ToArray();
            var name = node.Name;
            result.CheckStructure(() => node.Name == name && node.Children.SequenceEqual(children) &&
                node.Items.SequenceEqual(items) && node.Assets.SequenceEqual(assets));
            // Snapshot active choices before touching asset paths, including references outside the asset list.
            foreach (var type in AssetFolderTypes)
            {
                var captured = type;
                result.Add(() => node.GetPrimaryAssetPath(captured), value => node.SetActiveAsset(captured, value),
                    oldPrefix, newPrefix, renamedFiles);
            }
            foreach (var asset in node.Assets)
                if (seenAssets.Add(asset))
                    result.Add(() => asset.RelativePath, value => asset.RelativePath = value, oldPrefix, newPrefix, renamedFiles);
            foreach (var item in node.Items)
            {
                if (!seenItems.Add(item)) continue;
                var itemAssets = item.Assets.ToArray();
                result.CheckStructure(() => item.Assets.SequenceEqual(itemAssets));
                var beforeCount = result.Count;
                foreach (var type in AssetFolderTypes)
                {
                    var captured = type;
                    result.Add(() => item.GetPrimaryAssetPath(captured), value => item.SetActiveAsset(captured, value),
                        oldPrefix, newPrefix, renamedFiles);
                }
                foreach (var asset in item.Assets)
                    if (seenAssets.Add(asset))
                        result.Add(() => asset.RelativePath, value => asset.RelativePath = value, oldPrefix, newPrefix, renamedFiles);
                if (result.Count != beforeCount) result.AddItem(item);
            }
        }
        return result;
    }

    private static bool TryMapRenamedPath(string path, IReadOnlyDictionary<string, string>? renamedFiles, out string mapped)
    {
        mapped = string.Empty;
        if (renamedFiles == null || renamedFiles.Count == 0 ||
            !renamedFiles.TryGetValue(NormalizeRelativePath(path), out var value)) return false;
        mapped = value;
        return true;
    }

    private static string ReplaceRelativePrefix(string path, string oldPrefix, string newPrefix)
    {
        var normalizedPath = NormalizeRelativePath(path);
        var normalizedOld = NormalizeRelativePath(oldPrefix);
        var normalizedNew = NormalizeRelativePath(newPrefix);
        if (string.Equals(normalizedPath, normalizedOld, StringComparison.Ordinal)) return normalizedNew;
        var oldWithSlash = normalizedOld + "/";
        return normalizedPath.StartsWith(oldWithSlash, StringComparison.Ordinal)
            ? normalizedNew + "/" + normalizedPath[oldWithSlash.Length..] : path;
    }
}
