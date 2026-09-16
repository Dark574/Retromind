using System;
using System.Collections.Generic;

namespace Retromind.Services.RetroAchievements;

public sealed class RetroAchievementsGameProgress
{
    public int GameId { get; init; }
    public string Title { get; init; } = string.Empty;
    public uint ConsoleId { get; init; }
    public string ConsoleName { get; init; } = string.Empty;
    public string? ImageIconPath { get; init; }
    public string? ImageTitlePath { get; init; }
    public string? ImageInGamePath { get; init; }
    public string? ImageBoxArtPath { get; init; }
    public int AchievementCount { get; init; }
    public int AwardedCount { get; init; }
    public int AwardedHardcoreCount { get; init; }
    public double CompletionPercent { get; init; }
    public double CompletionHardcorePercent { get; init; }
    public int UserTotalPlaytime { get; init; }
    public string? HighestAwardKind { get; init; }
    public DateTimeOffset? HighestAwardAtUtc { get; init; }
    public IReadOnlyList<RetroAchievementsAchievement> Achievements { get; init; } =
        Array.Empty<RetroAchievementsAchievement>();
}
