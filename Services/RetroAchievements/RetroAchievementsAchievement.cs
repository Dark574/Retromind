using System;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsAchievement
{
    public int AchievementId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int Points { get; init; }
    public int TrueRatio { get; init; }
    public string Author { get; init; } = string.Empty;
    public string BadgeName { get; init; } = string.Empty;
    public int DisplayOrder { get; init; }
    public string? Type { get; init; }
    public DateTimeOffset? EarnedAtUtc { get; init; }
    public DateTimeOffset? EarnedHardcoreAtUtc { get; init; }

    public bool IsEarned => EarnedAtUtc.HasValue;
    public bool IsEarnedHardcore => EarnedHardcoreAtUtc.HasValue;
}
