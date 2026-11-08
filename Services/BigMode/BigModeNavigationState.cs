using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Services.BigMode;

/// <summary>
/// Detached session bookmark for a future Home/settings adapter. This does not
/// change AppSettings or persist anything, and contains no model references.
/// </summary>
public sealed class BigModeNavigationState
{
    public BigModeNavigationState(IEnumerable<string> enteredNodeIds,
        BigModeNavigationKey? selectedKey, int selectedIndexHint = -1)
    {
        ArgumentNullException.ThrowIfNull(enteredNodeIds);
        EnteredNodeIds = Array.AsReadOnly(enteredNodeIds.ToArray());
        SelectedKey = selectedKey;
        SelectedIndexHint = selectedIndexHint;
    }

    public IReadOnlyList<string> EnteredNodeIds { get; }
    public BigModeNavigationKey? SelectedKey { get; }
    public int SelectedIndexHint { get; }
}

/// <summary>
/// Navigation never starts a launcher or closes a window. The adapter handles
/// explicit media/exit requests; a NodeOpened result reflects an already opened frame.
/// </summary>
public abstract record BigModeNavigationResult
{
    private BigModeNavigationResult() { }

    public sealed record NoAction : BigModeNavigationResult;
    public sealed record NodeOpened(BigModeNavigationFrame Frame) : BigModeNavigationResult;
    public sealed record MediaActivationRequested(BigModeMediaNavigationEntry Entry) : BigModeNavigationResult;
    public sealed record ExitRequested : BigModeNavigationResult;
}
