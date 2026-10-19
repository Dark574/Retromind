using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed class GogGameProgress
{
    public string GameId { get; init; } = string.Empty;
    public IReadOnlyList<GogAchievement> Achievements { get; init; } =
        Array.Empty<GogAchievement>();

    public int AchievementCount => Achievements.Count;
    public int UnlockedCount => Achievements.Count(achievement => achievement.IsUnlocked);
    public int LockedHiddenAchievementCount => Achievements.Count(
        achievement => achievement.IsHidden && !achievement.IsUnlocked);
    public double CompletionPercent => AchievementCount == 0
        ? 0
        : UnlockedCount * 100d / AchievementCount;
}
