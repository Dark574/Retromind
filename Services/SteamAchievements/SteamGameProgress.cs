using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Services.SteamAchievements;

public sealed class SteamGameProgress
{
    public uint AppId { get; init; }
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<SteamAchievement> Achievements { get; init; } =
        Array.Empty<SteamAchievement>();

    public int AchievementCount => Achievements.Count;
    public int UnlockedCount => Achievements.Count(achievement => achievement.IsUnlocked);
    public int HiddenAchievementCount => Achievements.Count(achievement => achievement.IsHidden);
    public int LockedHiddenAchievementCount => Achievements.Count(
        achievement => achievement.IsHidden && !achievement.IsUnlocked);
    public double CompletionPercent => AchievementCount == 0
        ? 0
        : UnlockedCount * 100d / AchievementCount;
}
