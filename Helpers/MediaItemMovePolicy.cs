using Retromind.Models;

namespace Retromind.Helpers;

internal enum MediaItemMoveTargetStatus
{
    Allowed,
    CurrentNode
}

internal sealed record MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus Status)
{
    public bool IsAllowed => Status == MediaItemMoveTargetStatus.Allowed;
}

internal static class MediaItemMovePolicy
{
    public static MediaItemMoveTargetAssessment Assess(
        MediaNode sourceNode,
        MediaNode targetNode)
    {
        if (ReferenceEquals(sourceNode, targetNode) || sourceNode.Id == targetNode.Id)
            return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.CurrentNode);

        return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.Allowed);
    }
}
