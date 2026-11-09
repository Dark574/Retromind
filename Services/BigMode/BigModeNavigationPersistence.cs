using System.Linq;
using Retromind.Models;

namespace Retromind.Services.BigMode;

public static class BigModeNavigationPersistence
{
    public static void Restore(BigModeNavigationSession session, AppSettings settings)
    {
        if (settings.BigModeNavigation is { Version: 1 } saved)
        {
            var key = saved.SelectedMediaId != null
                ? new BigModeNavigationKey(BigModeNavigationEntryKind.MediaItem, saved.SelectedMediaId)
                : saved.SelectedNodeId != null ? new BigModeNavigationKey(BigModeNavigationEntryKind.Node, saved.SelectedNodeId)
                : (BigModeNavigationKey?)null;
            session.RestoreState(new(saved.EnteredNodeIds ?? [], key, saved.SelectedIndexHint));
            return;
        }

        var legacyKey = settings.LastBigModeSelectedNodeId is { Length: > 0 } id
            ? new BigModeNavigationKey(settings.LastBigModeWasItemView
                ? BigModeNavigationEntryKind.MediaItem : BigModeNavigationEntryKind.Node, id)
            : (BigModeNavigationKey?)null;
        if (settings.LastBigModeNavigationPath is { Count: > 0 } path)
            session.RestoreState(new(path, legacyKey));
        else if (legacyKey != null)
            session.RestoreState(new([], legacyKey));
        else if (settings.LastSelectedMediaId is { Length: > 0 } mediaId &&
                 session.TryReveal(new(BigModeNavigationEntryKind.MediaItem, mediaId))) { }
        else if (settings.LastSelectedNodeId is { Length: > 0 } nodeId)
            session.TryOpenNode(new(BigModeNavigationEntryKind.Node, nodeId));
    }

    public static void Save(BigModeNavigationSession session, AppSettings settings)
    {
        var state = session.CaptureState();
        settings.BigModeNavigation = new()
        {
            EnteredNodeIds = state.EnteredNodeIds.ToList(),
            SelectedIndexHint = state.SelectedIndexHint,
            SelectedNodeId = state.SelectedKey is { Kind: BigModeNavigationEntryKind.Node } node ? node.Id : null,
            SelectedMediaId = state.SelectedKey is { Kind: BigModeNavigationEntryKind.MediaItem } media ? media.Id : null
        };
        settings.LastBigModeNavigationPath = state.EnteredNodeIds.ToList();
        settings.LastBigModeSelectedNodeId = state.SelectedKey?.Id;
        settings.LastBigModeWasItemView = state.SelectedKey?.Kind == BigModeNavigationEntryKind.MediaItem;
        settings.LastSelectedNodeId = session.Current.SelectedEntry is BigModeMediaNavigationEntry item
            ? item.SourceNode.Id : (session.Current.SelectedEntry as BigModeNodeNavigationEntry)?.Node.Id
              ?? session.Current.OpenedNode?.Id;
        settings.LastSelectedMediaId = settings.BigModeNavigation.SelectedMediaId;
    }

    /// <summary>Record desktop intent without reindexing the library on every grid selection.
    /// The next session resolves the real owner once, through its existing snapshot.</summary>
    public static void RecordDesktopSelection(AppSettings settings, MediaNode node, MediaItem? item)
    {
        settings.BigModeNavigation = null;
        settings.LastBigModeNavigationPath = null;
        settings.LastBigModeSelectedNodeId = null;
        settings.LastBigModeWasItemView = false;
        settings.LastSelectedNodeId = node.Id;
        settings.LastSelectedMediaId = item?.Id;
    }
}
