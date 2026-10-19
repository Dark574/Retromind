using System;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed class GogAchievement
{
    public string AchievementId { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public bool IsUnlocked { get; init; }
    public DateTimeOffset? UnlockedAtUtc { get; init; }
    public string? ImageUrl { get; init; }
    public double Rarity { get; init; }
    public string RarityLevelDescription { get; init; } = string.Empty;
}
