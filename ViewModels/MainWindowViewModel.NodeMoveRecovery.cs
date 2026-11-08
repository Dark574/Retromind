using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private string? _nodeAssetRecoveryMessage;
    public string? NodeAssetRecoveryMessage
    {
        get => _nodeAssetRecoveryMessage;
        private set => SetProperty(ref _nodeAssetRecoveryMessage, value);
    }

    // Called by both drag/drop paths on the UI thread. Reporting already belongs to the move caller.
    internal void ShowNodeMoveRecoveryIfNeeded()
    {
        if (LastNodeAssetOperation is not { RollbackComplete: false } recovery) return;
        var location = recovery.RecoveryManifest ?? recovery.StagingDirectory ?? recovery.TargetPrefix;
        var message = string.Format(T("NodeAssets.MoveRecoveryRequired",
            "Category move failed. Manual recovery is required. Preserve files at (relative to the data directory): {0}"), location);
        // An unrelated rejection/cancellation must not dismiss earlier unresolved recovery information.
        if (NodeAssetRecoveryMessage == null) NodeAssetRecoveryMessage = message;
        else if (!NodeAssetRecoveryMessage.Contains(message, StringComparison.Ordinal))
            NodeAssetRecoveryMessage += "\n\n" + message;
    }

    private sealed class NodeAssignmentSnapshot
    {
        private readonly List<Action> _restore = new();
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public NodeAssignmentSnapshot(MediaNode source, MediaNode target,
            ObservableCollection<MediaNode> sourceParent, ObservableCollection<MediaNode> destination)
        {
            Capture(sourceParent, new[] { source });
            Capture(destination, new[] { source }.Concat(source.Children).ToArray());
            Capture(source.Assets, source.Assets.ToArray());
            Capture(target.Assets, source.Assets.ToArray());
            Capture(source.Items, source.Items.ToArray());
            Capture(target.Items, source.Items.ToArray());
            Capture(source.Children, source.Children.ToArray());
            Capture(target.Children, source.Children.ToArray());
        }

        private void Capture<T>(ObservableCollection<T> collection, T[] participants) where T : class
        {
            if (!_seen.Add(collection)) return;
            var before = collection.ToArray();
            _restore.Add(() =>
            {
                // Remove only additions made from the known source; preserve unexpected concurrent entries.
                for (var index = collection.Count - 1; index >= 0; index--)
                {
                    var entry = collection[index];
                    if (participants.Any(value => ReferenceEquals(value, entry)) &&
                        collection.Count(value => ReferenceEquals(value, entry)) > before.Count(value => ReferenceEquals(value, entry)))
                        collection.RemoveAt(index);
                }
                for (var index = 0; index < before.Length; index++)
                {
                    var current = -1;
                    for (var position = index; position < collection.Count; position++)
                        if (ReferenceEquals(collection[position], before[index])) { current = position; break; }
                    if (current < 0) collection.Insert(Math.Min(index, collection.Count), before[index]);
                    else if (current != index) collection.Move(current, index);
                }
                if (collection.Count != before.Length)
                    throw new InvalidOperationException("Concurrent category entries were retained during recovery.");
            });
        }

        public void Restore()
        {
            var failures = new List<Exception>();
            foreach (var restore in _restore.AsEnumerable().Reverse())
            {
                try { restore(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new AggregateException("Category metadata recovery was incomplete.", failures);
        }
    }
}
