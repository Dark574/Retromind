using System.Collections.Generic;

namespace Retromind.Models;

/// <summary>Versioned navigation location. Model objects and theme presentation are not persisted.</summary>
public sealed class BigModeNavigationBookmark
{
    public int Version { get; set; } = 1;
    public List<string> EnteredNodeIds { get; set; } = [];
    public int SelectedIndexHint { get; set; }
    public string? SelectedNodeId { get; set; }
    public string? SelectedMediaId { get; set; }
}
