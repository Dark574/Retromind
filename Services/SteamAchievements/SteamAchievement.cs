using System;

namespace Retromind.Services.SteamAchievements;

/// <summary>
/// User-specific state combined with the public presentation data for one
/// Steam achievement. Locked hidden achievements intentionally contain no
/// title, description, or image URL.
/// </summary>
public sealed class SteamAchievement
{
    public string ApiName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public bool IsUnlocked { get; init; }
    public DateTimeOffset? UnlockedAtUtc { get; init; }
    public string? ImageUrl { get; init; }
}
