using System;
using System.Collections.Generic;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsGameProgress
{
    public int GameId { get; init; }
    public string Title { get; init; } = string.Empty;
    public uint ConsoleId { get; init; }
    public string ConsoleName { get; init; } = string.Empty;
    public int AchievementCount { get; init; }
    public int AwardedCount { get; init; }
    public int AwardedHardcoreCount { get; init; }
    public double CompletionPercent { get; init; }
    public double CompletionHardcorePercent { get; init; }
    public int UserTotalPlaytime { get; init; }
    public IReadOnlyList<RetroAchievementsAchievement> Achievements { get; init; } =
        Array.Empty<RetroAchievementsAchievement>();
}
