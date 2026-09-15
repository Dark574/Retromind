namespace Retromind.Models;

/// <summary>
/// Provider-neutral identity of an emulated game system.
/// Provider-specific IDs are mapped separately by each integration.
/// </summary>
public sealed record GameSystemDefinition(string Id, string DisplayName);
